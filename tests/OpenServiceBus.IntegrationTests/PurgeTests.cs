using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Purge (issue #36) through the real SDK: every message-shaped thing disappears while
/// topology, live processors, and held session locks stay intact - the between-test reset
/// story for long-lived brokers. See docs/Purge.md.
/// </summary>
public class PurgeTests
{
    [Fact]
    public async Task PurgeQueue_RemovesActiveScheduledDeferredAndDeadLetteredMessages()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.CreateQueueAsync("p");
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("p");
        await sender.SendMessageAsync(new ServiceBusMessage("active"));
        await sender.SendMessageAsync(new ServiceBusMessage("to-defer"));
        await sender.SendMessageAsync(new ServiceBusMessage("to-dlq"));
        await sender.ScheduleMessageAsync(new ServiceBusMessage("scheduled"), DateTimeOffset.UtcNow.AddMinutes(5));

        var receiver = client.CreateReceiver("p");
        var toDefer = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        await receiver.DeferMessageAsync(toDefer!);
        var toDlq = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        await receiver.DeadLetterMessageAsync(toDlq!, "test", "sent to the dlq before the purge");

        var purged = await host.PurgeQueueAsync("p");

        purged.ShouldBe(4L);
        (await host.Store.CountAsync("p")).ShouldBe(0L);
        (await host.Store.CountAsync("p/$DeadLetterQueue")).ShouldBe(0L);
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull();

        await sender.SendMessageAsync(new ServiceBusMessage("after-purge"));
        var after = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        after.ShouldNotBeNull();
        after.Body.ToString().ShouldBe("after-purge");
        await receiver.CompleteMessageAsync(after);
    }

    [Fact]
    public async Task PurgeAll_UnderALiveProcessor_ProcessorKeepsRunning()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.CreateQueueAsync("worker");
        await using var client = new ServiceBusClient(host.ConnectionString);

        var processed = new List<string>();
        var processor = client.CreateProcessor("worker");
        processor.ProcessMessageAsync += args =>
        {
            lock (processed) processed.Add(args.Message.Body.ToString());
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync();

        var sender = client.CreateSender("worker");
        await sender.SendMessageAsync(new ServiceBusMessage("before"));
        await WaitForAsync(() => { lock (processed) return processed.Contains("before"); });

        await host.PurgeAllAsync();

        await sender.SendMessageAsync(new ServiceBusMessage("after"));
        await WaitForAsync(() => { lock (processed) return processed.Contains("after"); });

        await processor.StopProcessingAsync();
        await processor.DisposeAsync();
    }

    [Fact]
    public async Task Purge_WithAMessageInFlight_SettlingItThrowsMessageLockLost()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.CreateQueueAsync("inflight");
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("inflight").SendMessageAsync(new ServiceBusMessage("held"));
        var receiver = client.CreateReceiver("inflight");
        var held = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        held.ShouldNotBeNull();

        (await host.PurgeQueueAsync("inflight")).ShouldBe(1L);

        // The purge removed the locked message, so settling it fails exactly like an
        // expired lock would - MessageLockLost, not a silent no-op (issue #52).
        var ex = await Should.ThrowAsync<ServiceBusException>(() => receiver.CompleteMessageAsync(held));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessageLockLost);

        await client.CreateSender("inflight").SendMessageAsync(new ServiceBusMessage("still-works"));
        var after = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        after.ShouldNotBeNull();
        await receiver.CompleteMessageAsync(after);
    }

    [Fact]
    public async Task PurgeQueue_WhileASessionIsAccepted_TheSessionLockSurvives()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.CreateQueueAsync(new QueueDescriptor { Name = "sessions", RequiresSession = true });
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("sessions");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { SessionId = "s-1" });
        var session = await client.AcceptSessionAsync("sessions", "s-1");
        var first = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        await session.CompleteMessageAsync(first!);

        await host.PurgeQueueAsync("sessions");

        var stolen = await Should.ThrowAsync<ServiceBusException>(
            () => client.AcceptSessionAsync("sessions", "s-1"));
        stolen.Reason.ShouldBe(ServiceBusFailureReason.SessionCannotBeLocked);

        await sender.SendMessageAsync(new ServiceBusMessage("second") { SessionId = "s-1" });
        var second = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        second.ShouldNotBeNull();
        second.Body.ToString().ShouldBe("second");
        await session.CompleteMessageAsync(second);
        await session.CloseAsync();
    }

    [Fact]
    public async Task PurgeTopic_ClearsEverySubscription()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "a" });
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "b" });
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("one"));
        await sender.SendMessageAsync(new ServiceBusMessage("two"));

        (await host.PurgeTopicAsync("events")).ShouldBe(4L);
        (await host.Store.CountAsync("events/Subscriptions/a")).ShouldBe(0L);
        (await host.Store.CountAsync("events/Subscriptions/b")).ShouldBe(0L);

        await sender.SendMessageAsync(new ServiceBusMessage("three"));
        var receiver = client.CreateReceiver("events", "a");
        var after = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        after.ShouldNotBeNull();
        after.Body.ToString().ShouldBe("three");
        await receiver.CompleteMessageAsync(after);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        condition().ShouldBeTrue("timed out waiting for the condition");
    }
}
