using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// The publish-side sequence number (issue #55): stored messages carry the sequence number
/// assigned by the entity they were originally sent to, preserved across forward and
/// fan-out re-enqueues so deliveries can stamp <c>x-opt-enqueue-sequence-number</c>.
/// </summary>
public class EnqueuedSequenceNumberTests
{
    private static readonly byte[] Payload = [0x01];

    private static MessageFilterContext Msg() => new()
    {
        ApplicationProperties = new Dictionary<string, object?>(),
        EnqueuedTimeUtc = DateTimeOffset.UtcNow,
    };

    private static (MessageRouter Router, QueueManager Queues, TopicManager Topics, InMemoryMessageStore Store) NewFixture()
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues, store);
        var router = new MessageRouter(queues, store, NullLogger<MessageRouter>.Instance, topics);
        return (router, queues, topics, store);
    }

    [Fact]
    public async Task Enqueue_FreshSend_EnqueuedSequenceNumberEqualsSequenceNumber()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");

        var first = await store.EnqueueAsync("q", Payload);
        var second = await store.EnqueueAsync("q", Payload);

        first.EnqueuedSequenceNumber.ShouldBe(first.SequenceNumber);
        second.EnqueuedSequenceNumber.ShouldBe(second.SequenceNumber);
        second.SequenceNumber.ShouldBe(2L);
    }

    [Fact]
    public async Task Enqueue_ExplicitOriginalSequence_IsPreservedThroughDequeue()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");

        var stored = await store.EnqueueAsync("q", Payload, enqueuedSequenceNumber: 42L);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));

        stored.SequenceNumber.ShouldBe(1L);
        stored.EnqueuedSequenceNumber.ShouldBe(42L);
        locked.ShouldNotBeNull();
        locked.Message.EnqueuedSequenceNumber.ShouldBe(42L);
    }

    [Fact]
    public async Task AllocateSequenceNumber_SharesTheCounterWithEnqueue()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");

        var allocated = await store.AllocateSequenceNumberAsync("q");
        var stored = await store.EnqueueAsync("q", Payload);

        allocated.ShouldBe(1L);
        stored.SequenceNumber.ShouldBe(2L);
    }

    [Fact]
    public async Task Abandon_Redelivery_KeepsTheOriginalEnqueuedSequenceNumber()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", Payload, enqueuedSequenceNumber: 7L);

        var first = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        await store.TryAbandonAsync("q", first!.LockToken);
        var second = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));

        second.ShouldNotBeNull();
        second.Message.DeliveryCount.ShouldBe(1);
        second.Message.EnqueuedSequenceNumber.ShouldBe(7L);
    }

    [Fact]
    public async Task ScheduledActivation_KeepsTheOriginalEnqueuedSequenceNumber()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        var scheduledFor = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.EnqueueAsync("q", Payload, scheduledEnqueueTime: scheduledFor, enqueuedSequenceNumber: 9L);

        store.ActivateScheduled("q", scheduledFor.AddSeconds(1));
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));

        locked.ShouldNotBeNull();
        locked.Message.EnqueuedSequenceNumber.ShouldBe(9L);
    }

    [Fact]
    public async Task FanOut_AllCopiesShareOnePublishSideSequenceNumber()
    {
        var (router, _, topics, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sub1 = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "s1" });
        var sub2 = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "s2" });

        await router.RouteAsync("events", Payload, filterContext: Msg());
        await router.RouteAsync("events", Payload, filterContext: Msg());

        var copies1 = store.Peek(sub1.BackingQueueName, 0, 10);
        var copies2 = store.Peek(sub2.BackingQueueName, 0, 10);
        copies1.Select(m => m.EnqueuedSequenceNumber).ShouldBe(new[] { 1L, 2L });
        copies2.Select(m => m.EnqueuedSequenceNumber).ShouldBe(new[] { 1L, 2L });
        copies1[0].EnqueuedSequenceNumber.ShouldBe(copies2[0].EnqueuedSequenceNumber);
    }

    [Fact]
    public async Task FanOut_PublishSideCounterIsIndependentOfTheBackingQueues()
    {
        var (router, _, topics, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "s1" });
        await store.EnqueueAsync(sub.BackingQueueName, Payload);
        await store.EnqueueAsync(sub.BackingQueueName, Payload);

        await router.RouteAsync("events", Payload, filterContext: Msg());

        var copy = store.Peek(sub.BackingQueueName, 0, 10)[^1];
        copy.SequenceNumber.ShouldBe(3L);
        copy.EnqueuedSequenceNumber.ShouldBe(1L, "first publish on the topic gets publish-side sequence #1");
    }

    [Fact]
    public async Task ForwardChain_UsesTheOriginalQueuesCounter()
    {
        var (router, queues, _, store) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "target" });
        await queues.CreateAsync(new QueueDescriptor { Name = "front", ForwardTo = "target" });
        await store.EnqueueAsync("target", Payload);

        await router.RouteAsync("front", Payload, filterContext: Msg());

        var copy = store.Peek("target", 0, 10)[^1];
        copy.SequenceNumber.ShouldBe(2L);
        copy.EnqueuedSequenceNumber.ShouldBe(1L, "the front queue allocated publish-side sequence #1");
    }

    [Fact]
    public async Task ForwardChain_ExplicitOriginal_IsNotReallocatedAtIntermediateHops()
    {
        var (router, queues, _, store) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "target" });
        await queues.CreateAsync(new QueueDescriptor { Name = "middle", ForwardTo = "target" });

        await router.RouteAsync("middle", Payload, filterContext: Msg(), enqueuedSequenceNumber: 77L);

        var copy = store.Peek("target", 0, 10)[0];
        copy.EnqueuedSequenceNumber.ShouldBe(77L);
    }

    [Fact]
    public async Task SubscriptionForward_CopyKeepsThePublishSideSequenceNumber()
    {
        var (router, queues, topics, store) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "audit" });
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events", Name = "fwd", ForwardTo = "audit",
        });
        await store.EnqueueAsync("audit", Payload);
        await store.EnqueueAsync("audit", Payload);

        await router.RouteAsync("events", Payload, filterContext: Msg());

        var copy = store.Peek("audit", 0, 10)[^1];
        copy.SequenceNumber.ShouldBe(3L);
        copy.EnqueuedSequenceNumber.ShouldBe(1L, "the topic allocated publish-side sequence #1");
    }
}
