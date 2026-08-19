using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Verifies that the Service Bus SDK reads the broker-stamped system properties
/// (delivery count, enqueued time, sequence number, locked-until) back to non-default values.
/// </summary>
public class SystemPropertiesTests
{
    [Fact]
    public async Task ReceiveMessageAsync_FreshlyEnqueuedMessage_ExposesBrokerStampedSystemProperties()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "sys-props",
            LockDuration = TimeSpan.FromSeconds(45),
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var beforeSend = DateTimeOffset.UtcNow;
        var sender = client.CreateSender("sys-props");
        await sender.SendMessageAsync(new ServiceBusMessage("hello") { MessageId = "id-1" });
        var receiver = client.CreateReceiver("sys-props", new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });

        // Act
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // Assert
        msg.ShouldNotBeNull();
        msg.MessageId.ShouldBe("id-1");
        msg.DeliveryCount.ShouldBe(1, "the Azure SDK reports DeliveryCount as attempts (1-indexed) where wire delivery-count is 0");
        msg.SequenceNumber.ShouldBe(1L);
        msg.EnqueuedTime.ShouldBeGreaterThanOrEqualTo(beforeSend.AddSeconds(-1));
        msg.EnqueuedTime.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
        msg.LockedUntil.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(30),
            "configured lock duration is 45s so locked-until should be ~45s out");

        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task AbandonMessageAsync_RepeatedAbandons_IncrementsDeliveryCountAcrossRedeliveries()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "retry-counts" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("retry-counts");
        await sender.SendMessageAsync(new ServiceBusMessage("retry") { MessageId = "m-1" });
        var receiver = client.CreateReceiver("retry-counts", new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });

        // Act
        var a = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        a.ShouldNotBeNull();
        await receiver.AbandonMessageAsync(a);
        var b = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        b.ShouldNotBeNull();
        await receiver.AbandonMessageAsync(b);
        var c = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        c.ShouldNotBeNull();
        await receiver.CompleteMessageAsync(c);

        // Assert
        a.DeliveryCount.ShouldBe(1);
        b.DeliveryCount.ShouldBe(2);
        c.DeliveryCount.ShouldBe(3);
    }

    [Fact]
    public async Task ReceiveMessageAsync_DirectSend_ExposesEnqueuedSequenceNumberStateAndPartitionKeys()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "sys-props-full" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("sys-props-full");
        await sender.SendMessageAsync(new ServiceBusMessage("hello")
        {
            MessageId = "id-1",
            PartitionKey = "pk-1",
            TransactionPartitionKey = "pk-1",
        });
        var receiver = client.CreateReceiver("sys-props-full");

        // Act
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // Assert
        msg.ShouldNotBeNull();
        msg.SequenceNumber.ShouldBe(1L);
        msg.EnqueuedSequenceNumber.ShouldBe(1L, "a direct send's EnqueuedSequenceNumber equals its SequenceNumber");
        msg.State.ShouldBe(ServiceBusMessageState.Active);
        msg.PartitionKey.ShouldBe("pk-1");
        msg.TransactionPartitionKey.ShouldBe("pk-1");

        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task PeekMessageAsync_ExposesEnqueuedSequenceNumberAndPartitionKey()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "peek-props" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("peek-props");
        await sender.SendMessageAsync(new ServiceBusMessage("hello") { MessageId = "id-1", PartitionKey = "pk-1" });
        var receiver = client.CreateReceiver("peek-props");

        // Act
        var peeked = await receiver.PeekMessageAsync();

        // Assert
        peeked.ShouldNotBeNull();
        peeked.SequenceNumber.ShouldBe(1L);
        peeked.EnqueuedSequenceNumber.ShouldBe(1L);
        peeked.State.ShouldBe(ServiceBusMessageState.Active);
        peeked.PartitionKey.ShouldBe("pk-1");
    }

    [Fact]
    public async Task ScheduledMessage_PeeksAsScheduledAndArrivesWithOriginalScheduledTimeAndActiveState()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "sched-props" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("sched-props");
        var scheduledFor = DateTimeOffset.UtcNow.AddSeconds(2);
        var seq = await sender.ScheduleMessageAsync(new ServiceBusMessage("later") { MessageId = "id-1" }, scheduledFor);
        var receiver = client.CreateReceiver("sched-props");

        // Act - peek while still scheduled, then receive once activated.
        var peeked = await receiver.PeekMessageAsync(fromSequenceNumber: seq);
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15));

        // Assert
        peeked.ShouldNotBeNull();
        peeked.State.ShouldBe(ServiceBusMessageState.Scheduled);
        peeked.EnqueuedSequenceNumber.ShouldBe(seq);

        msg.ShouldNotBeNull();
        msg.State.ShouldBe(ServiceBusMessageState.Active, "an activated message delivers as Active");
        msg.SequenceNumber.ShouldBe(seq);
        msg.EnqueuedSequenceNumber.ShouldBe(seq);
        msg.ScheduledEnqueueTime.ShouldBe(scheduledFor, TimeSpan.FromMilliseconds(50),
            "the receiver must see the original scheduled enqueue time");
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task ReceiveDeferredMessageAsync_ExposesDeferredStateAndEnqueuedSequenceNumber()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "defer-props" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("defer-props");
        await sender.SendMessageAsync(new ServiceBusMessage("park me") { MessageId = "id-1" });
        var receiver = client.CreateReceiver("defer-props");
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        first.ShouldNotBeNull();
        await receiver.DeferMessageAsync(first);

        // Act
        var deferred = await receiver.ReceiveDeferredMessageAsync(first.SequenceNumber);

        // Assert
        deferred.ShouldNotBeNull();
        deferred.State.ShouldBe(ServiceBusMessageState.Deferred);
        deferred.EnqueuedSequenceNumber.ShouldBe(first.SequenceNumber);
        deferred.GetRawAmqpMessage().DeliveryAnnotations.TryGetValue("x-opt-lock-token", out var tokenAnnotation)
            .ShouldBeTrue("the Python SDK settles deferred messages by the x-opt-lock-token delivery annotation");
        tokenAnnotation.ShouldBe(Guid.Parse(deferred.LockToken));
        await receiver.CompleteMessageAsync(deferred);
    }

    [Fact]
    public async Task AutoForward_ReceiverSeesTheSourceQueuesEnqueuedSequenceNumber()
    {
        // Arrange - pre-fill the target so its local sequence numbers run ahead of the source's.
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "fwd-target" });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "fwd-source", ForwardTo = "fwd-target" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var directSender = client.CreateSender("fwd-target");
        await directSender.SendMessageAsync(new ServiceBusMessage("filler") { MessageId = "filler-1" });
        await directSender.SendMessageAsync(new ServiceBusMessage("filler") { MessageId = "filler-2" });
        var forwardSender = client.CreateSender("fwd-source");
        await forwardSender.SendMessageAsync(new ServiceBusMessage("forward me") { MessageId = "fwd-1" });
        var receiver = client.CreateReceiver("fwd-target");

        // Act - drain until the forwarded message arrives.
        ServiceBusReceivedMessage? forwarded = null;
        while (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5)) is { } msg)
        {
            await receiver.CompleteMessageAsync(msg);
            if (msg.MessageId == "fwd-1")
            {
                forwarded = msg;
                break;
            }
        }

        // Assert
        forwarded.ShouldNotBeNull();
        forwarded.SequenceNumber.ShouldBe(3L, "two fillers landed on the target first");
        forwarded.EnqueuedSequenceNumber.ShouldBe(1L, "the source queue assigned its own first sequence number");
    }

    [Fact]
    public async Task TopicFanOut_CopiesShareThePublishSideEnqueuedSequenceNumber()
    {
        // Arrange - s2 is created between the two publishes, so its local sequence numbers
        // diverge from the topic's publish-side numbering.
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "fan-props" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "fan-props", Name = "s1" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("fan-props");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "m-1" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "fan-props", Name = "s2" });
        await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "m-2" });

        // Act
        var r1 = client.CreateReceiver("fan-props", "s1");
        var s1First = await r1.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        var s1Second = await r1.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        var r2 = client.CreateReceiver("fan-props", "s2");
        var s2Second = await r2.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // Assert
        s1First.ShouldNotBeNull();
        s1Second.ShouldNotBeNull();
        s2Second.ShouldNotBeNull();
        s1First.EnqueuedSequenceNumber.ShouldBe(1L);
        s1Second.EnqueuedSequenceNumber.ShouldBe(2L);
        s2Second.MessageId.ShouldBe("m-2");
        s2Second.SequenceNumber.ShouldBe(1L, "the second publish is s2's first local message");
        s2Second.EnqueuedSequenceNumber.ShouldBe(2L, "the copy keeps the topic's publish-side sequence number");
        s2Second.EnqueuedSequenceNumber.ShouldBe(s1Second.EnqueuedSequenceNumber);
    }

    [Fact]
    public async Task ScheduledTopicPublish_ActivatedCopiesKeepTheTopicsSequenceNumberAndScheduledTime()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "sched-fan" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "sched-fan", Name = "s1" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("sched-fan");
        var scheduledFor = DateTimeOffset.UtcNow.AddSeconds(2);
        var topicSeq = await sender.ScheduleMessageAsync(new ServiceBusMessage("held") { MessageId = "id-1" }, scheduledFor);

        // Act
        var receiver = client.CreateReceiver("sched-fan", "s1");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15));

        // Assert
        msg.ShouldNotBeNull();
        msg.EnqueuedSequenceNumber.ShouldBe(topicSeq, "the fan-out copy keeps the topic-held publish's sequence number");
        msg.ScheduledEnqueueTime.ShouldBe(scheduledFor, TimeSpan.FromMilliseconds(50));
        msg.State.ShouldBe(ServiceBusMessageState.Active);
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task DeadLetteredMessage_KeepsTheOriginalEnqueuedSequenceNumber()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "esn-dlq" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("esn-dlq");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "m-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "m-2" });
        var receiver = client.CreateReceiver("esn-dlq");
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        await receiver.CompleteMessageAsync(first);
        var second = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        await receiver.DeadLetterMessageAsync(second, "why-not");

        // Act
        var dlqReceiver = client.CreateReceiver("esn-dlq", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        var moved = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // Assert
        moved.ShouldNotBeNull();
        moved.MessageId.ShouldBe("m-2");
        moved.SequenceNumber.ShouldBe(1L, "the DLQ assigns its own sequence number");
        moved.EnqueuedSequenceNumber.ShouldBe(2L, "the moved message keeps the sequence it had on the source queue");
        await dlqReceiver.CompleteMessageAsync(moved);
    }

    [Fact]
    public async Task SessionReceive_ExposesEnqueuedSequenceNumberAndActiveState()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "esn-sessions", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("esn-sessions");
        await sender.SendMessageAsync(new ServiceBusMessage("hi") { MessageId = "m-1", SessionId = "sess-1" });

        // Act
        var receiver = await client.AcceptSessionAsync("esn-sessions", "sess-1");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // Assert
        msg.ShouldNotBeNull();
        msg.EnqueuedSequenceNumber.ShouldBe(msg.SequenceNumber);
        msg.State.ShouldBe(ServiceBusMessageState.Active);
        await receiver.CompleteMessageAsync(msg);
    }
}
