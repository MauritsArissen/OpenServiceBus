using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Transfer dead-letter queue (issue #25) through the real SDK: forward failures land in
/// <c>&lt;entity&gt;/$Transfer/$DeadLetterQueue</c>, receivable via
/// <see cref="SubQueue.TransferDeadLetter"/>, with counts on runtime properties.
/// </summary>
public class TransferDeadLetterTests
{
    [Fact]
    public async Task Forward_ToDeletedTarget_IsReceivableFromTheTransferDlq()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateQueueAsync("tdlq-b");
        await admin.CreateQueueAsync(new CreateQueueOptions("tdlq-a") { ForwardTo = "tdlq-b" });
        await admin.DeleteQueueAsync("tdlq-b");
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("tdlq-a").SendMessageAsync(
            new ServiceBusMessage("undeliverable") { MessageId = "m-1" });

        var receiver = client.CreateReceiver("tdlq-a",
            new ServiceBusReceiverOptions { SubQueue = SubQueue.TransferDeadLetter });
        var moved = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

        moved.ShouldNotBeNull();
        moved.MessageId.ShouldBe("m-1");
        moved.Body.ToString().ShouldBe("undeliverable");
        moved.DeadLetterReason.ShouldBe("MessagingEntityNotFound");
        moved.DeadLetterErrorDescription.ShouldNotBeNullOrEmpty();
        moved.DeadLetterSource.ShouldBe("tdlq-a");
        await receiver.CompleteMessageAsync(moved);
    }

    [Fact]
    public async Task Forward_ToDisabledTarget_LandsInTheTransferDlq()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateQueueAsync("gate-b");
        await admin.CreateQueueAsync(new CreateQueueOptions("gate-a") { ForwardTo = "gate-b" });
        QueueProperties target = await admin.GetQueueAsync("gate-b");
        target.Status = EntityStatus.SendDisabled;
        await admin.UpdateQueueAsync(target);
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("gate-a").SendMessageAsync(new ServiceBusMessage("blocked"));

        var receiver = client.CreateReceiver("gate-a",
            new ServiceBusReceiverOptions { SubQueue = SubQueue.TransferDeadLetter });
        var moved = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        moved.ShouldNotBeNull();
        moved.DeadLetterReason.ShouldBe("MessagingEntityDisabled");
        await receiver.CompleteMessageAsync(moved);
    }

    [Fact]
    public async Task ForwardCycle_ExceedingTheHopCap_LandsInATransferDlq()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.Queues.CreateAsync(new Core.Entities.QueueDescriptor { Name = "loop-a", ForwardTo = "loop-b" });
        await host.Queues.CreateAsync(new Core.Entities.QueueDescriptor { Name = "loop-b", ForwardTo = "loop-a" });
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("loop-a").SendMessageAsync(new ServiceBusMessage("round and round"));

        var counts = new[]
        {
            await host.Store.CountAsync("loop-a/$Transfer/$DeadLetterQueue"),
            await host.Store.CountAsync("loop-b/$Transfer/$DeadLetterQueue"),
        };
        counts.Sum().ShouldBe(1L, "the looping message must land in exactly one transfer DLQ");
        (await host.Store.CountAsync("loop-a")).ShouldBe(0L);
        (await host.Store.CountAsync("loop-b")).ShouldBe(0L);
    }

    [Fact]
    public async Task SubscriptionForward_ToDeletedTarget_IsReceivableViaTheSubscriptionTransferDlq()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateQueueAsync("sub-fwd-target");
        await admin.CreateTopicAsync("tdlq-topic");
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions("tdlq-topic", "fwd")
        {
            ForwardTo = "sub-fwd-target",
        });
        await admin.DeleteQueueAsync("sub-fwd-target");
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("tdlq-topic").SendMessageAsync(new ServiceBusMessage("fan-out then fail"));

        var receiver = client.CreateReceiver("tdlq-topic", "fwd",
            new ServiceBusReceiverOptions { SubQueue = SubQueue.TransferDeadLetter });
        var moved = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        moved.ShouldNotBeNull();
        moved.DeadLetterReason.ShouldBe("MessagingEntityNotFound");
        moved.DeadLetterSource.ShouldBe("tdlq-topic/Subscriptions/fwd");
        await receiver.CompleteMessageAsync(moved);
    }

    [Fact]
    public async Task TransferDeadLetterCounts_AreReportedOnRuntimeProperties()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateQueueAsync("count-b");
        await admin.CreateQueueAsync(new CreateQueueOptions("count-a") { ForwardTo = "count-b" });
        await admin.DeleteQueueAsync("count-b");
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("count-a").SendMessageAsync(new ServiceBusMessage("one"));
        await client.CreateSender("count-a").SendMessageAsync(new ServiceBusMessage("two"));

        QueueRuntimeProperties runtime = await admin.GetQueueRuntimePropertiesAsync("count-a");
        runtime.TransferDeadLetterMessageCount.ShouldBe(2);
        runtime.TotalMessageCount.ShouldBe(2);
    }

    [Fact]
    public async Task ForwardDeadLetteredMessagesTo_MissingTarget_LandsInTheTransferDlq()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.Queues.CreateAsync(new Core.Entities.QueueDescriptor
        {
            Name = "fdlm-src",
            ForwardDeadLetteredMessagesTo = "fdlm-target",
        });
        await using var client = new ServiceBusClient(host.ConnectionString);

        await client.CreateSender("fdlm-src").SendMessageAsync(new ServiceBusMessage("dead on arrival"));
        var receiver = client.CreateReceiver("fdlm-src");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        await receiver.DeadLetterMessageAsync(msg!, "test", "sent while the DLQ forward target is missing");

        var transfer = client.CreateReceiver("fdlm-src",
            new ServiceBusReceiverOptions { SubQueue = SubQueue.TransferDeadLetter });
        var moved = await transfer.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        moved.ShouldNotBeNull();
        moved.DeadLetterReason.ShouldBe("MessagingEntityNotFound");
        await transfer.CompleteMessageAsync(moved);
    }
}
