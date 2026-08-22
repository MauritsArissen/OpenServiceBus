using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

public class ReceiverCloseLockSurvivalTests
{
    [Fact]
    public async Task ClosingAReceiver_WithAnUnsettledMessage_KeepsTheLockUntilItExpires()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "survivor",
            LockDuration = TimeSpan.FromSeconds(5),
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("survivor").SendMessageAsync(new ServiceBusMessage("held") { MessageId = "sv-1" });

        var first = client.CreateReceiver("survivor");
        var msg = await first.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        msg.DeliveryCount.ShouldBe(1);

        // Act
        await first.CloseAsync();

        // Assert
        var second = client.CreateReceiver("survivor");
        var whileLocked = await second.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        whileLocked.ShouldBeNull("the lock must survive the receiver close, exactly like Azure");

        await Task.Delay(TimeSpan.FromSeconds(4));
        harness.Store.ExpireLocks("survivor", DateTimeOffset.UtcNow).ShouldBe(1,
            "the orphaned lock must still be in place until the expiry sweep reclaims it");
        var afterExpiry = await second.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        afterExpiry.ShouldNotBeNull("the message must redeliver once the abandoned receiver's lock expires");
        afterExpiry.MessageId.ShouldBe("sv-1");
        afterExpiry.DeliveryCount.ShouldBe(2);
        await second.CompleteMessageAsync(afterExpiry);
    }
}
