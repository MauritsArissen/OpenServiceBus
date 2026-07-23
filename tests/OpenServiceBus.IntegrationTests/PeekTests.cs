using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// PeekMessage(s) over <c>$management com.microsoft:peek-message</c> - read without locking.
/// Verifies sequence-number stamping and the Scheduled vs Active <c>x-opt-message-state</c>.
/// </summary>
public class PeekTests
{
    [Fact]
    public async Task PeekMessagesAsync_ThreeMessagesInQueue_ReturnsAllInSequenceOrderWithoutLocking()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "peeky" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("peeky");
        for (var i = 1; i <= 3; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"body-{i}") { MessageId = $"m-{i}" });
        }
        var receiver = client.CreateReceiver("peeky");

        // Act
        var peeked = await receiver.PeekMessagesAsync(maxMessages: 5);
        var recv = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // Assert
        peeked.Count.ShouldBe(3);
        peeked.Select(m => m.MessageId).ShouldBe(new[] { "m-1", "m-2", "m-3" });
        peeked.Select(m => m.SequenceNumber).ShouldBe(new[] { 1L, 2L, 3L });
        peeked.All(m => m.State == ServiceBusMessageState.Active).ShouldBeTrue();
        recv.ShouldNotBeNull();
        recv.MessageId.ShouldBe("m-1", "peek did not consume - receive still gets the first message");
        await receiver.CompleteMessageAsync(recv);
    }

    [Fact]
    public async Task PeekMessagesAsync_ScheduledMessage_ReportsScheduledMessageState()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "peek-sched" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("peek-sched");
        await sender.ScheduleMessageAsync(
            new ServiceBusMessage("future") { MessageId = "soon" },
            DateTimeOffset.UtcNow.AddMinutes(5));
        var receiver = client.CreateReceiver("peek-sched");

        // Act
        var peeked = await receiver.PeekMessagesAsync(maxMessages: 5);

        // Assert
        peeked.Count.ShouldBe(1);
        peeked[0].MessageId.ShouldBe("soon");
        peeked[0].State.ShouldBe(ServiceBusMessageState.Scheduled);
    }

    [Fact]
    public async Task PeekMessageAsync_EmptyQueue_ReturnsNull()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "empty-peek" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var receiver = client.CreateReceiver("empty-peek");

        // Act
        var msg = await receiver.PeekMessageAsync();

        // Assert
        msg.ShouldBeNull();
    }

    [Fact]
    public async Task PeekMessagesAsync_OnDeadLetterQueue_ExposesDeliveryCountAndDeadLetterReason()
    {
        // Regression: peeked messages carried no AMQP header, so reading
        // ServiceBusReceivedMessage.DeliveryCount on a peeked message threw
        // "Nullable object must have a value".

        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "peek-dlq" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("peek-dlq").SendMessageAsync(new ServiceBusMessage("poison") { MessageId = "p-1" });

        var receiver = client.CreateReceiver("peek-dlq");
        var fresh = await receiver.PeekMessageAsync();
        fresh.ShouldNotBeNull();
        fresh!.DeliveryCount.ShouldBe(0, "an undelivered message peeks with delivery count 0");

        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        await receiver.DeadLetterMessageAsync(msg!, "TestReason", "test description");

        // Act
        var dlqReceiver = client.CreateReceiver("peek-dlq", new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
        });
        var peeked = await dlqReceiver.PeekMessagesAsync(maxMessages: 10);

        // Assert
        peeked.Count.ShouldBe(1);
        peeked[0].MessageId.ShouldBe("p-1");
        peeked[0].DeliveryCount.ShouldBeGreaterThanOrEqualTo(1);
        peeked[0].DeadLetterReason.ShouldBe("TestReason");
        peeked[0].DeadLetterErrorDescription.ShouldBe("test description");
    }
}
