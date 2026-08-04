using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Message size limits and entity quotas (issue #24) through the real SDK: oversized
/// transfers surface as <see cref="ServiceBusFailureReason.MessageSizeExceeded"/>, full
/// entities as <see cref="ServiceBusFailureReason.QuotaExceeded"/>, and the per-entity
/// <c>max-message-size</c> on link attach drives <c>ServiceBusMessageBatch</c> sizing.
/// </summary>
public class SizeLimitTests
{
    [Fact]
    public async Task Send300KbToDefaultQueue_ThrowsMessageSizeExceeded()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "standard" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => client.CreateSender("standard").SendMessageAsync(new ServiceBusMessage(new byte[300 * 1024])));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessageSizeExceeded);
    }

    [Fact]
    public async Task LargerPerEntityLimit_AllowsBiggerMessages()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "roomy", MaxMessageSizeInKilobytes = 1024 });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        await client.CreateSender("roomy").SendMessageAsync(new ServiceBusMessage(new byte[300 * 1024]));

        var receiver = client.CreateReceiver("roomy");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull();
        received.Body.ToMemory().Length.ShouldBe(300 * 1024);
        await receiver.CompleteMessageAsync(received);
    }

    [Fact]
    public async Task QuotaExceeded_OnFullQueue_FreesUpAfterCompleting()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "tiny", MaxSizeInMegabytes = 1 });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("tiny");

        var payload = new byte[200 * 1024];
        for (var i = 0; i < 5; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage(payload));
        }

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => sender.SendMessageAsync(new ServiceBusMessage(payload)));
        ex.Reason.ShouldBe(ServiceBusFailureReason.QuotaExceeded);

        var receiver = client.CreateReceiver("tiny", new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
        });
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10))).ShouldNotBeNull();
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10))).ShouldNotBeNull();
        for (var i = 0; i < 50 && harness.Store.GetSizeInBytes("tiny") > 4 * 200 * 1024; i++)
        {
            await Task.Delay(50);
        }

        await sender.SendMessageAsync(new ServiceBusMessage(payload));
    }

    [Fact]
    public async Task MessageBatch_SizesItselfFromTheAdvertisedLinkLimit()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "batchy", MaxMessageSizeInKilobytes = 100 });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("batchy");

        using var batch = await sender.CreateMessageBatchAsync();
        batch.MaxSizeInBytes.ShouldBe(100 * 1024);
        batch.TryAddMessage(new ServiceBusMessage(new byte[40 * 1024])).ShouldBeTrue();
        batch.TryAddMessage(new ServiceBusMessage(new byte[40 * 1024])).ShouldBeTrue();
        batch.TryAddMessage(new ServiceBusMessage(new byte[40 * 1024])).ShouldBeFalse("a third 40 KB message cannot fit the 100 KB link limit");

        await sender.SendMessagesAsync(batch);
        var receiver = client.CreateReceiver("batchy");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10))).ShouldNotBeNull();
    }

    [Fact]
    public async Task TopicQuota_CoversSubscriptionBackingQueues()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "narrow", MaxSizeInMegabytes = 1 });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "narrow", Name = "s" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("narrow");

        var payload = new byte[200 * 1024];
        for (var i = 0; i < 5; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage(payload));
        }

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => sender.SendMessageAsync(new ServiceBusMessage(payload)));
        ex.Reason.ShouldBe(ServiceBusFailureReason.QuotaExceeded);
    }

    [Fact]
    public async Task SizeProperties_RoundTrip_ThroughTheAdminClient()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        await admin.CreateQueueAsync(new CreateQueueOptions("sized")
        {
            MaxSizeInMegabytes = 2048,
            MaxMessageSizeInKilobytes = 512,
        });

        QueueProperties fetched = await admin.GetQueueAsync("sized");
        fetched.MaxSizeInMegabytes.ShouldBe(2048);
        fetched.MaxMessageSizeInKilobytes.ShouldBe(512);

        var descriptor = await harness.Queues.GetAsync("sized");
        descriptor!.MaxSizeInMegabytes.ShouldBe(2048);
        descriptor.MaxMessageSizeInKilobytes.ShouldBe(512);

        await admin.CreateTopicAsync(new CreateTopicOptions("sized-topic") { MaxSizeInMegabytes = 3072 });
        ((TopicProperties)await admin.GetTopicAsync("sized-topic")).MaxSizeInMegabytes.ShouldBe(3072);
    }
}
