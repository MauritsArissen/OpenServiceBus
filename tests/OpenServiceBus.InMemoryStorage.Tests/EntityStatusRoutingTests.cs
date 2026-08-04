using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class EntityStatusRoutingTests
{
    private static readonly byte[] Payload = [0x01];

    private static MessageFilterContext Msg() => new()
    {
        ApplicationProperties = new Dictionary<string, object?>(),
        EnqueuedTimeUtc = DateTimeOffset.UtcNow,
    };

    private static async Task<(MessageRouter Router, TopicManager Topics, InMemoryMessageStore Store)> Fixture()
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues);
        var router = new MessageRouter(queues, store, NullLogger<MessageRouter>.Instance, topics);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        return (router, topics, store);
    }

    [Fact]
    public async Task FanOut_SendDisabledSubscription_ReceivesNoNewCopies()
    {
        var (router, topics, store) = await Fixture();
        var frozen = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events", Name = "frozen", Status = EntityStatus.SendDisabled,
        });
        var active = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events", Name = "active",
        });

        var landed = await router.RouteAsync("events", Payload, filterContext: Msg());

        landed.ShouldBe(new[] { active.BackingQueueName });
        (await store.CountAsync(frozen.BackingQueueName)).ShouldBe(0L);
        (await store.CountAsync(active.BackingQueueName)).ShouldBe(1L);
    }

    [Fact]
    public async Task FanOut_DisabledSubscription_StillAccruesCopies()
    {
        var (router, topics, store) = await Fixture();
        var disabled = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events", Name = "disabled", Status = EntityStatus.Disabled,
        });

        await router.RouteAsync("events", Payload, filterContext: Msg());

        (await store.CountAsync(disabled.BackingQueueName)).ShouldBe(1L,
            "Disabled freezes the subscription for receive but must not lose fanned-out copies");
    }

    [Fact]
    public async Task SubscriptionStatus_MirrorsOntoTheBackingQueue_OnCreateAndUpdate()
    {
        var queues = new QueueManager(new InMemoryMessageStore());
        var topics = new TopicManager(queues);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });

        var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events", Name = "s", Status = EntityStatus.ReceiveDisabled,
        });
        (await queues.GetAsync(sub.BackingQueueName))!.Status.ShouldBe(EntityStatus.ReceiveDisabled);

        await topics.UpdateSubscriptionAsync(sub with { Status = EntityStatus.Active });
        (await queues.GetAsync(sub.BackingQueueName))!.Status.ShouldBe(EntityStatus.Active);
    }
}
