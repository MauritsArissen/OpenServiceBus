using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Core.Entities;
using SdkStatus = Azure.Messaging.ServiceBus.Administration.EntityStatus;
using BrokerStatus = OpenServiceBus.Core.Entities.EntityStatus;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Entity status (issue #22) through the real Azure SDK: Disabled / SendDisabled /
/// ReceiveDisabled are enforced on the send and receive paths and surface as
/// <see cref="ServiceBusFailureReason.MessagingEntityDisabled"/>, and status round-trips
/// through the admin client.
/// </summary>
public class EntityStatusTests
{
    [Fact]
    public async Task UpdateQueueAsync_Disabled_SendThrowsMessagingEntityDisabled()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("lockdown");

        QueueProperties props = await admin.GetQueueAsync("lockdown");
        props.Status = SdkStatus.Disabled;
        await admin.UpdateQueueAsync(props);

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateSender("lockdown").SendMessageAsync(new ServiceBusMessage("no entry")));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityDisabled);
    }

    [Fact]
    public async Task SendDisabled_BlocksSends_ButDrainsExistingMessages()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "draining" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("draining").SendMessageAsync(new ServiceBusMessage("pre-freeze") { MessageId = "d-1" });

        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        QueueProperties props = await admin.GetQueueAsync("draining");
        props.Status = SdkStatus.SendDisabled;
        await admin.UpdateQueueAsync(props);

        var sendEx = await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateSender("draining").SendMessageAsync(new ServiceBusMessage("rejected")));
        sendEx.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityDisabled);

        var receiver = client.CreateReceiver("draining");
        var drained = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        drained.ShouldNotBeNull("SendDisabled must keep the receive path open for draining");
        drained.MessageId.ShouldBe("d-1");
        await receiver.CompleteMessageAsync(drained);
    }

    [Fact]
    public async Task ReceiveDisabled_BlocksReceivers_ButAcceptsSends()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "frozen", Status = BrokerStatus.ReceiveDisabled });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        await client.CreateSender("frozen").SendMessageAsync(new ServiceBusMessage("banked") { MessageId = "f-1" });
        (await harness.Store.CountAsync("frozen")).ShouldBe(1L);

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateReceiver("frozen").ReceiveMessageAsync(TimeSpan.FromSeconds(5)));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityDisabled);
    }

    [Fact]
    public async Task Status_RoundTrips_ThroughTheAdminClient()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        await admin.CreateQueueAsync(new CreateQueueOptions("statusful") { Status = SdkStatus.ReceiveDisabled });
        ((QueueProperties)await admin.GetQueueAsync("statusful")).Status.ShouldBe(SdkStatus.ReceiveDisabled);

        await admin.CreateTopicAsync(new CreateTopicOptions("status-topic") { Status = SdkStatus.SendDisabled });
        ((TopicProperties)await admin.GetTopicAsync("status-topic")).Status.ShouldBe(SdkStatus.SendDisabled);

        QueueProperties queue = await admin.GetQueueAsync("statusful");
        queue.Status = SdkStatus.Active;
        await admin.UpdateQueueAsync(queue);
        ((QueueProperties)await admin.GetQueueAsync("statusful")).Status.ShouldBe(SdkStatus.Active);

        (await harness.Queues.GetAsync("statusful"))!.Status.ShouldBe(BrokerStatus.Active);
    }

    [Fact]
    public async Task DisabledTopic_RejectsPublishes()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "dark", Status = BrokerStatus.Disabled });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "dark", Name = "s" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateSender("dark").SendMessageAsync(new ServiceBusMessage("void")));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityDisabled);
    }

    [Fact]
    public async Task ReceiveDisabledSubscription_RejectsReceivers_TopicStillAcceptsPublishes()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sub = await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events",
            Name = "held",
            Status = BrokerStatus.ReceiveDisabled,
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        await client.CreateSender("events").SendMessageAsync(new ServiceBusMessage("queued up"));
        (await harness.Store.CountAsync(sub.BackingQueueName)).ShouldBe(1L,
            "ReceiveDisabled must not stop fan-out into the subscription");

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateReceiver("events", "held").ReceiveMessageAsync(TimeSpan.FromSeconds(5)));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityDisabled);
    }

    [Fact]
    public async Task ScheduleMessageAsync_OnSendDisabledQueue_IsRejected()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "no-schedule", Status = BrokerStatus.SendDisabled });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateSender("no-schedule").ScheduleMessageAsync(
                new ServiceBusMessage("later"), DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
