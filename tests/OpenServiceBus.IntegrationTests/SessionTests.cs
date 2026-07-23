using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// The Azure SDK's session receiver claims a session over AMQP, receives only
/// messages belonging to that session in order, round-trips per-session state, renews the
/// session lock, and releases on dispose so another receiver can take over.
/// </summary>
public class SessionTests
{
    [Fact]
    public async Task CreateSessionReceiverAsync_SpecificSession_OnlyReceivesMessagesWithMatchingSessionId()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "orders", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("orders");

        await sender.SendMessageAsync(new ServiceBusMessage("a-1") { SessionId = "alpha", MessageId = "a-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("b-1") { SessionId = "beta", MessageId = "b-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("a-2") { SessionId = "alpha", MessageId = "a-2" });

        // Act - loop until the session drain returns null.
        var receiver = await client.AcceptSessionAsync("orders", "alpha");
        var ids = new List<string>();
        while (true)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500));
            if (msg is null) break;
            ids.Add(msg.MessageId);
            await receiver.CompleteMessageAsync(msg);
        }

        // Assert
        ids.ToArray().ShouldBe(new[] { "a-1", "a-2" }, "session 'alpha' messages only, in enqueue order");
    }

    [Fact]
    public async Task SetSessionStateAsync_GetSessionStateAsync_RoundTripsBlobOverAmqp()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "stateful", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("stateful");
        await sender.SendMessageAsync(new ServiceBusMessage("hi") { SessionId = "s1" });
        var receiver = await client.AcceptSessionAsync("stateful", "s1");

        // Act
        var payload = new BinaryData(new byte[] { 1, 2, 3, 4 });
        await receiver.SetSessionStateAsync(payload);
        var read = await receiver.GetSessionStateAsync();

        // Assert
        read.ToArray().ShouldBe(new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task AcceptSessionAsync_AlreadyLocked_ThrowsSessionCannotBeLocked()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "exclusive", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("exclusive");
        await sender.SendMessageAsync(new ServiceBusMessage("hi") { SessionId = "only-one" });
        var first = await client.AcceptSessionAsync("exclusive", "only-one");

        // Act + Assert
        var ex = await Should.ThrowAsync<ServiceBusException>(() =>
            client.AcceptSessionAsync("exclusive", "only-one"));
        ex.Reason.ShouldBe(ServiceBusFailureReason.SessionCannotBeLocked);

        // After closing the first, the session frees up.
        await first.DisposeAsync();
        for (var i = 0; i < 20; i++)
        {
            try
            {
                var second = await client.AcceptSessionAsync("exclusive", "only-one");
                await second.DisposeAsync();
                return;
            }
            catch (ServiceBusException) when (i < 19)
            {
                await Task.Delay(50);
            }
        }
        throw new Exception("releasing the first receiver didn't free the session");
    }

    [Fact]
    public async Task AcceptNextSessionAsync_TwoSessions_HandsOutEachOnceUnderConcurrentReceivers()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "fanned", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("fanned");
        await sender.SendMessageAsync(new ServiceBusMessage("x") { SessionId = "s1" });
        await sender.SendMessageAsync(new ServiceBusMessage("y") { SessionId = "s2" });

        // Act
        var r1 = await client.AcceptNextSessionAsync("fanned");
        var r2 = await client.AcceptNextSessionAsync("fanned");

        // Assert
        new[] { r1.SessionId, r2.SessionId }.OrderBy(s => s).ToArray().ShouldBe(new[] { "s1", "s2" });
    }

    [Fact]
    public async Task RenewSessionLockAsync_ExtendsTheSessionLock()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "longwork",
            RequiresSession = true,
            LockDuration = TimeSpan.FromSeconds(30),
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("longwork");
        await sender.SendMessageAsync(new ServiceBusMessage("slow") { SessionId = "s" });
        var receiver = await client.AcceptSessionAsync("longwork", "s");
        var before = receiver.SessionLockedUntil;

        // Act
        await receiver.RenewSessionLockAsync();

        // Assert
        receiver.SessionLockedUntil.ShouldBeGreaterThan(before, "renew must push the session lock forward");
    }

    [Fact]
    public async Task SendAndReceive_PlainQueue_StillWorksWithSessionInfrastructurePresent()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "regression" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("regression");
        await sender.SendMessageAsync(new ServiceBusMessage("hello") { MessageId = "id-1" });
        var receiver = client.CreateReceiver("regression");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        msg!.MessageId.ShouldBe("id-1");
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task Send_WithoutSessionId_ToSessionQueue_IsRejected()
    {
        // Regression: the broker used to accept the send and route it to the sessionless
        // channel, where no session receiver can ever read it - a silent black hole. Azure
        // rejects with amqp:not-allowed, surfaced by the SDK as InvalidOperationException.

        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "strict-sessions", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("strict-sessions");

        // Act + Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sender.SendMessageAsync(new ServiceBusMessage("no session")));
        ex.Message.ShouldContain("SessionId");
        (await harness.Store.CountAsync("strict-sessions")).ShouldBe(0L);

        // A message WITH a session id still goes through.
        await sender.SendMessageAsync(new ServiceBusMessage("ok") { SessionId = "s1" });
        (await harness.Store.CountAsync("strict-sessions")).ShouldBe(1L);
    }

    [Fact]
    public async Task ScheduleMessage_WithoutSessionId_ToSessionQueue_IsRejected()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "strict-sched", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("strict-sched");

        // Act + Assert
        var ex = await Should.ThrowAsync<Exception>(
            () => sender.ScheduleMessageAsync(new ServiceBusMessage("no session"), DateTimeOffset.UtcNow.AddMinutes(1)));
        ex.Message.ShouldContain("SessionId");
        (await harness.Store.CountAsync("strict-sched")).ShouldBe(0L);
    }

    [Fact]
    public async Task ScheduledMessage_WithGroupId_OnPlainQueue_IsStillDeliveredNormally()
    {
        // Regression: the schedule path honored GroupId even on non-session queues, so the
        // activated message landed in a session channel no plain receiver reads.

        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "plain-sched" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("plain-sched").ScheduleMessageAsync(
            new ServiceBusMessage("tagged") { SessionId = "ignored-on-plain-queues", MessageId = "gp-1" },
            DateTimeOffset.UtcNow.AddSeconds(1));

        // Act
        var receiver = client.CreateReceiver("plain-sched");
        ServiceBusReceivedMessage? msg = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline && msg is null)
        {
            msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500));
        }

        // Assert
        msg.ShouldNotBeNull("a plain queue must deliver scheduled messages regardless of GroupId");
        msg!.MessageId.ShouldBe("gp-1");
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task AcceptNextSessionAsync_SessionArrivesWhileWaiting_IsPickedUpByTheParkedRequest()
    {
        // Azure parks an accept-next-session server-side until a session appears or the
        // conveyed timeout elapses. The broker used to reject immediately, forcing the SDK
        // into client-side retry loops.

        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "parked", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        // Act: start the accept on an EMPTY queue, then send the first session message
        // while the accept is parked broker-side.
        var acceptTask = client.AcceptNextSessionAsync("parked");
        await Task.Delay(500);
        acceptTask.IsCompleted.ShouldBeFalse("the broker should hold the accept open, not answer instantly");

        await client.CreateSender("parked").SendMessageAsync(
            new ServiceBusMessage("late arrival") { SessionId = "night-owl" });

        var receiver = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        receiver.SessionId.ShouldBe("night-owl");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        msg!.Body.ToString().ShouldBe("late arrival");
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task AcceptNextSessionAsync_NoSessionsAvailable_SurfacesServiceTimeout()
    {
        // Regression: the broker used to refuse the attach with a custom error symbol the
        // SDK maps to GeneralError, which sent ServiceBusSessionProcessor into a hot retry
        // loop. Real Service Bus answers com.microsoft:timeout → ServiceTimeout, which the
        // processor treats as a quiet "no sessions right now, poll again".

        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "no-sessions", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(2) },
        });

        // Act
        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => client.AcceptNextSessionAsync("no-sessions"));

        // Assert
        ex.Reason.ShouldBe(ServiceBusFailureReason.ServiceTimeout);
    }
}
