using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// PropertiesToModify on abandon / defer / dead-letter through the real Azure SDK (issue
/// #30): the map rides the disposition (or the $management update-disposition when settling
/// a deferred message) and must be merged into the stored message so redeliveries, deferred
/// retrievals and DLQ copies carry it.
/// </summary>
public class PropertiesToModifyTests
{
    private static async Task<IntegrationHarness> StartWithQueueAsync(string name, bool requiresSession = false)
    {
        var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = name, RequiresSession = requiresSession });
        return harness;
    }

    [Fact]
    public async Task Abandon_WithProperties_RedeliversWithMergedPropertiesAndBumpedDeliveryCount()
    {
        await using var harness = await StartWithQueueAsync("ptm-abandon");
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("ptm-abandon");
        var original = new ServiceBusMessage("payload") { MessageId = "a-1" };
        original.ApplicationProperties["existing"] = "old";
        original.ApplicationProperties["untouched"] = "keep";
        await sender.SendMessageAsync(original);

        var receiver = client.CreateReceiver("ptm-abandon");
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        first.ShouldNotBeNull();
        await receiver.AbandonMessageAsync(first, new Dictionary<string, object>
        {
            ["retry-reason"] = "timeout",
            ["existing"] = "new",
        });

        var second = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        second.ShouldNotBeNull();
        second.DeliveryCount.ShouldBe(2);
        second.ApplicationProperties["retry-reason"].ShouldBe("timeout");
        second.ApplicationProperties["existing"].ShouldBe("new", "last write wins per key");
        second.ApplicationProperties["untouched"].ShouldBe("keep");
        second.Body.ToString().ShouldBe("payload");
        await receiver.CompleteMessageAsync(second);
    }

    [Fact]
    public async Task Defer_WithProperties_DeferredRetrievalCarriesThem()
    {
        await using var harness = await StartWithQueueAsync("ptm-defer");
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("ptm-defer").SendMessageAsync(new ServiceBusMessage("d") { MessageId = "d-1" });

        var receiver = client.CreateReceiver("ptm-defer");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.DeferMessageAsync(msg, new Dictionary<string, object> { ["parked-because"] = "out-of-order" });

        var deferred = await receiver.ReceiveDeferredMessageAsync(msg.SequenceNumber);
        deferred.ShouldNotBeNull();
        deferred.ApplicationProperties["parked-because"].ShouldBe("out-of-order");
        await receiver.CompleteMessageAsync(deferred);
    }

    [Fact]
    public async Task DeadLetter_WithProperties_DlqCopyCarriesThemAlongsideReasonAndDescription()
    {
        await using var harness = await StartWithQueueAsync("ptm-dlq");
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("ptm-dlq").SendMessageAsync(new ServiceBusMessage("x") { MessageId = "dl-1" });

        var receiver = client.CreateReceiver("ptm-dlq");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.DeadLetterMessageAsync(msg,
            new Dictionary<string, object> { ["diagnostic"] = "schema-v1-mismatch" },
            "invalid-payload", "amount must be positive");

        var dlq = client.CreateReceiver("ptm-dlq", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var dead = await dlq.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        dead.ShouldNotBeNull();
        dead.DeadLetterReason.ShouldBe("invalid-payload");
        dead.DeadLetterErrorDescription.ShouldBe("amount must be positive");
        dead.ApplicationProperties["diagnostic"].ShouldBe("schema-v1-mismatch");
        await dlq.CompleteMessageAsync(dead);
    }

    [Fact]
    public async Task SettlingADeferredMessage_UsesTheManagementPath_AndStillMergesProperties()
    {
        // Settling a message obtained via ReceiveDeferredMessageAsync goes through the
        // $management update-disposition operation instead of a link disposition - the
        // "settle by lock token on a different path" case from the issue.
        await using var harness = await StartWithQueueAsync("ptm-mgmt");
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("ptm-mgmt").SendMessageAsync(new ServiceBusMessage("m") { MessageId = "mg-1" });

        var receiver = client.CreateReceiver("ptm-mgmt");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.DeferMessageAsync(msg);

        var deferred = await receiver.ReceiveDeferredMessageAsync(msg.SequenceNumber);
        await receiver.AbandonMessageAsync(deferred, new Dictionary<string, object> { ["mgmt-path"] = "yes" });

        var again = await receiver.ReceiveDeferredMessageAsync(msg.SequenceNumber);
        again.ShouldNotBeNull("abandon of a deferred message returns it to the deferred state");
        again.ApplicationProperties["mgmt-path"].ShouldBe("yes");
        await receiver.CompleteMessageAsync(again);
    }

    [Fact]
    public async Task DeadLetteringADeferredMessage_ViaTheManagementPath_CarriesProperties()
    {
        await using var harness = await StartWithQueueAsync("ptm-mgmt-dlq");
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("ptm-mgmt-dlq").SendMessageAsync(new ServiceBusMessage("m") { MessageId = "mg-2" });

        var receiver = client.CreateReceiver("ptm-mgmt-dlq");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.DeferMessageAsync(msg);
        var deferred = await receiver.ReceiveDeferredMessageAsync(msg.SequenceNumber);

        await receiver.DeadLetterMessageAsync(deferred,
            new Dictionary<string, object> { ["via"] = "management" }, "deferred-poison", null);

        var dlq = client.CreateReceiver("ptm-mgmt-dlq", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var dead = await dlq.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        dead.ShouldNotBeNull();
        dead.DeadLetterReason.ShouldBe("deferred-poison");
        dead.ApplicationProperties["via"].ShouldBe("management");
        await dlq.CompleteMessageAsync(dead);
    }

    [Fact]
    public async Task SessionReceiver_AbandonAndDeadLetterWithProperties_BehaveTheSame()
    {
        await using var harness = await StartWithQueueAsync("ptm-session", requiresSession: true);
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("ptm-session");
        await sender.SendMessageAsync(new ServiceBusMessage("one") { MessageId = "s-1", SessionId = "sess" });

        var session = await client.AcceptSessionAsync("ptm-session", "sess");
        var first = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        first.ShouldNotBeNull();
        await session.AbandonMessageAsync(first, new Dictionary<string, object> { ["session-retry"] = 1 });

        var redelivered = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        redelivered.ShouldNotBeNull();
        redelivered.ApplicationProperties["session-retry"].ShouldBe(1);
        await session.CompleteMessageAsync(redelivered);

        await sender.SendMessageAsync(new ServiceBusMessage("two") { MessageId = "s-2", SessionId = "sess" });
        var second = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        second.ShouldNotBeNull();
        await session.DeadLetterMessageAsync(second,
            new Dictionary<string, object> { ["session-poison"] = true }, "session-dlq", null);
        await session.CloseAsync();

        var dlq = client.CreateReceiver("ptm-session", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var dead = await dlq.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        dead.ShouldNotBeNull();
        dead.DeadLetterReason.ShouldBe("session-dlq");
        dead.ApplicationProperties["session-poison"].ShouldBe(true);
        await dlq.CompleteMessageAsync(dead);
    }
}
