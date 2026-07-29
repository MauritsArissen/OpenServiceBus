using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Sessions on topic subscriptions through the real Azure SDK (issue #18). In Service Bus,
/// sessions are a queue/subscription feature: topics have no session flag - publishers just
/// stamp <c>SessionId</c> on messages sent to the topic, and each session-enabled
/// subscription delivers them through exclusive session locks like a session queue would.
/// </summary>
public class TopicSessionTests
{
    private static async Task<IntegrationHarness> StartWithSessionSubscription(
        string topic = "events", string subscription = "sessions", bool requiresSession = true)
    {
        var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = topic });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = topic,
            Name = subscription,
            RequiresSession = requiresSession,
        });
        return harness;
    }

    [Fact]
    public async Task AcceptSessionAsync_OnSubscription_ReceivesOnlyThatSessionInOrder()
    {
        // Arrange
        await using var harness = await StartWithSessionSubscription();
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("a-1") { SessionId = "alpha", MessageId = "a-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("b-1") { SessionId = "beta", MessageId = "b-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("a-2") { SessionId = "alpha", MessageId = "a-2" });

        // Act - this attach previously came back as a plain receiver, crashing the SDK on the
        // missing session id / locked-until in the attach response (issue #18).
        var receiver = await client.AcceptSessionAsync("events", "sessions", "alpha");
        var ids = new List<string>();
        while (true)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500));
            if (msg is null) break;
            ids.Add(msg.MessageId);
            await receiver.CompleteMessageAsync(msg);
        }

        // Assert
        receiver.SessionId.ShouldBe("alpha");
        receiver.SessionLockedUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-5));
        ids.ToArray().ShouldBe(new[] { "a-1", "a-2" }, "session 'alpha' messages only, in publish order");
    }

    [Fact]
    public async Task AcceptNextSessionAsync_OnSubscription_ClaimsTheSessionWithMessages()
    {
        // Arrange
        await using var harness = await StartWithSessionSubscription();
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("events").SendMessageAsync(
            new ServiceBusMessage("standing order") { SessionId = "acct-42", MessageId = "so-1" });

        // Act
        var receiver = await client.AcceptNextSessionAsync("events", "sessions");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

        // Assert
        receiver.SessionId.ShouldBe("acct-42");
        msg.ShouldNotBeNull();
        msg.SessionId.ShouldBe("acct-42");
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task SessionMessage_ToTopicWithMixedSubscriptions_PlainSubscriptionStillReceives()
    {
        // Arrange - a session-enabled subscription and a plain sibling on the same topic.
        await using var harness = await StartWithSessionSubscription();
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "audit",
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("events").SendMessageAsync(
            new ServiceBusMessage("for both") { SessionId = "s-1", MessageId = "m-1" });

        // Act - the plain subscription reads with an ordinary receiver.
        var plainReceiver = client.CreateReceiver("events", "audit");
        var plainCopy = await plainReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

        // Assert - the copy is a normal message there (carrying its SessionId property), not
        // hidden in a session channel.
        plainCopy.ShouldNotBeNull("sessionless subscriptions must still receive session-stamped messages");
        plainCopy.MessageId.ShouldBe("m-1");
        plainCopy.SessionId.ShouldBe("s-1");
        await plainReceiver.CompleteMessageAsync(plainCopy);

        // And the session subscription delivers the same message through its session.
        var sessionReceiver = await client.AcceptSessionAsync("events", "sessions", "s-1");
        var sessionCopy = await sessionReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        sessionCopy.ShouldNotBeNull();
        sessionCopy.MessageId.ShouldBe("m-1");
        await sessionReceiver.CompleteMessageAsync(sessionCopy);
    }

    [Fact]
    public async Task SessionlessMessage_ToSessionSubscription_IsDeadLetteredNotStranded()
    {
        // Arrange
        await using var harness = await StartWithSessionSubscription();
        await using var client = new ServiceBusClient(harness.ConnectionString);

        // Act - publish WITHOUT a session id; the topic accepts it (unlike a session queue,
        // which rejects), and the session subscription's copy can never be delivered.
        await client.CreateSender("events").SendMessageAsync(new ServiceBusMessage("no session") { MessageId = "ns-1" });

        // Assert - the copy lands in the subscription's DLQ where it is visible and
        // recoverable, instead of sitting invisibly in the backing queue forever.
        var dlqReceiver = client.CreateReceiver("events", "sessions", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        var deadLettered = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        deadLettered.ShouldNotBeNull();
        deadLettered.MessageId.ShouldBe("ns-1");
        await dlqReceiver.CompleteMessageAsync(deadLettered);
        (await harness.Store.CountAsync("events/Subscriptions/sessions")).ShouldBe(0L);
    }

    [Fact]
    public async Task SessionState_AndLockRenewal_WorkOnSubscriptionSessions()
    {
        // Arrange
        await using var harness = await StartWithSessionSubscription();
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("events").SendMessageAsync(
            new ServiceBusMessage("stateful") { SessionId = "s-state" });

        var receiver = await client.AcceptSessionAsync("events", "sessions", "s-state");

        // Act + Assert - per-session state round-trips through the subscription's
        // $management node, and the session lock renews.
        await receiver.SetSessionStateAsync(BinaryData.FromString("checkpoint-7"));
        (await receiver.GetSessionStateAsync()).ToString().ShouldBe("checkpoint-7");

        var lockedUntilBefore = receiver.SessionLockedUntil;
        await receiver.RenewSessionLockAsync();
        receiver.SessionLockedUntil.ShouldBeGreaterThanOrEqualTo(lockedUntilBefore);

        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.CompleteMessageAsync(msg);
        await receiver.DisposeAsync();

        // The session (with state, no messages) is re-acceptable after release.
        var again = await client.AcceptSessionAsync("events", "sessions", "s-state");
        (await again.GetSessionStateAsync()).ToString().ShouldBe("checkpoint-7");
        await again.DisposeAsync();
    }

    [Fact]
    public async Task SessionProcessor_OnSubscription_ProcessesPerSessionInOrder()
    {
        // Arrange
        await using var harness = await StartWithSessionSubscription();
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("events");
        for (var i = 1; i <= 3; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"p-{i}") { SessionId = "proc", MessageId = $"p-{i}" });
        }

        var received = new List<string>();
        var done = new TaskCompletionSource();
        await using var processor = client.CreateSessionProcessor("events", "sessions", new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = 1,
            SessionIdleTimeout = TimeSpan.FromSeconds(2),
        });
        processor.ProcessMessageAsync += args =>
        {
            lock (received)
            {
                received.Add(args.Message.MessageId);
                if (received.Count == 3) done.TrySetResult();
            }
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        // Act
        await processor.StartProcessingAsync();
        (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)))).ShouldBe(done.Task,
            "the session processor must drain the subscription session");
        await processor.StopProcessingAsync();

        // Assert
        received.ToArray().ShouldBe(new[] { "p-1", "p-2", "p-3" }, "per-session FIFO order");
    }
}
