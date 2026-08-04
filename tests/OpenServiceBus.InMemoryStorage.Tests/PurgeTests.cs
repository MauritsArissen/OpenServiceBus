using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// Store-level purge (issue #36): every message-shaped thing goes, the queue itself and
/// live session locks stay. See docs/Purge.md.
/// </summary>
public class PurgeTests
{
    [Fact]
    public async Task Purge_RemovesActiveScheduledDeferredAndLockedMessages()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");

        await store.EnqueueAsync("q", new byte[] { 1 });
        await store.EnqueueAsync("q", new byte[] { 2 });
        await store.EnqueueAsync("q", new byte[] { 3 }, scheduledEnqueueTime: DateTimeOffset.UtcNow.AddHours(1));
        var toDefer = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        await store.TryDeferAsync("q", toDefer!.LockToken);
        var held = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));

        var purged = await store.PurgeAsync("q");

        purged.ShouldBe(3L);
        (await store.CountAsync("q")).ShouldBe(0L);
        (await store.TryReceiveDeferredAsync("q", toDefer.Message.SequenceNumber, TimeSpan.FromSeconds(30))).ShouldBeNull();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        (await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30), cancellationToken: cts.Token)).ShouldBeNull();
        (await store.TryCompleteAsync("q", held!.LockToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Purge_AfterPurge_TheQueueStaysFullyUsable()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 });
        await store.PurgeAsync("q");

        await store.EnqueueAsync("q", new byte[] { 2 });

        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        locked.ShouldNotBeNull();
        locked.Message.EncodedMessage.ShouldBe(new byte[] { 2 });
        (await store.TryCompleteAsync("q", locked.LockToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Purge_ClearsSessionMessagesAndState_ButKeepsTheSessionLock()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 }, sessionId: "s-1");
        var sessionLock = await store.TryAcceptSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-a");
        sessionLock.ShouldNotBeNull();
        await store.SetSessionStateAsync("q", "s-1", new byte[] { 0xAA });

        await store.PurgeAsync("q");

        (await store.GetSessionStateAsync("q", "s-1")).ShouldBeNull();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        (await store.TryDequeueFromSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-a", cts.Token)).ShouldBeNull();
        (await store.TryAcceptSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-b")).ShouldBeNull(
            "the purge must not steal a session lock a live receiver is holding");
    }

    [Fact]
    public async Task Purge_ClearsDuplicateDetectionHistory()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        var window = TimeSpan.FromMinutes(10);
        var original = await store.EnqueueAsync("q", new byte[] { 1 }, messageId: "m-1", duplicateDetectionWindow: window);

        await store.PurgeAsync("q");
        var resent = await store.EnqueueAsync("q", new byte[] { 2 }, messageId: "m-1", duplicateDetectionWindow: window);

        resent.SequenceNumber.ShouldNotBe(original.SequenceNumber,
            "after a purge the same MessageId must land as a fresh message, not be dropped as a duplicate");
        (await store.CountAsync("q")).ShouldBe(1L);
    }

    [Fact]
    public async Task Purge_UnknownQueue_ReturnsZeroWithoutThrowing()
    {
        var store = new InMemoryMessageStore();
        (await store.PurgeAsync("nope")).ShouldBe(0L);
    }
}

/// <summary>
/// <see cref="EntityPurger"/> composes store purges into entity semantics: queue + DLQ,
/// subscription backing queue + DLQ, topic across all subscriptions, and purge-all.
/// </summary>
public class EntityPurgerTests
{
    private static (EntityPurger Purger, TopicManager Topics, QueueManager Queues, InMemoryMessageStore Store) NewFixture()
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues);
        return (new EntityPurger(queues, store, topics), topics, queues, store);
    }

    [Fact]
    public async Task PurgeQueue_CoversTheQueueAndItsDeadLetterQueue()
    {
        var (purger, _, queues, store) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "orders" });
        await store.EnqueueAsync("orders", new byte[] { 1 });
        await store.EnqueueAsync("orders/$DeadLetterQueue", new byte[] { 2 });

        (await purger.PurgeQueueAsync("orders")).ShouldBe(2L);
        (await store.CountAsync("orders")).ShouldBe(0L);
        (await store.CountAsync("orders/$DeadLetterQueue")).ShouldBe(0L);
    }

    [Fact]
    public async Task PurgeQueue_DeadLetterOnly_LeavesTheMainQueueAlone()
    {
        var (purger, _, queues, store) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "orders" });
        await store.EnqueueAsync("orders", new byte[] { 1 });
        await store.EnqueueAsync("orders/$DeadLetterQueue", new byte[] { 2 });

        (await purger.PurgeQueueAsync("orders", deadLetterOnly: true)).ShouldBe(1L);
        (await store.CountAsync("orders")).ShouldBe(1L);
        (await store.CountAsync("orders/$DeadLetterQueue")).ShouldBe(0L);
    }

    [Fact]
    public async Task PurgeTopic_CoversEverySubscriptionBackingQueueAndTheirDlqs()
    {
        var (purger, topics, _, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "a" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "b" });
        await store.EnqueueAsync("events/Subscriptions/a", new byte[] { 1 });
        await store.EnqueueAsync("events/Subscriptions/b", new byte[] { 2 });
        await store.EnqueueAsync("events/Subscriptions/b/$DeadLetterQueue", new byte[] { 3 });

        (await purger.PurgeTopicAsync("events")).ShouldBe(3L);
        (await store.CountAsync("events/Subscriptions/a")).ShouldBe(0L);
        (await store.CountAsync("events/Subscriptions/b")).ShouldBe(0L);
        (await store.CountAsync("events/Subscriptions/b/$DeadLetterQueue")).ShouldBe(0L);
    }

    [Fact]
    public async Task PurgeSubscription_OnlyTouchesThatSubscription()
    {
        var (purger, topics, _, store) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "a" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "b" });
        await store.EnqueueAsync("events/Subscriptions/a", new byte[] { 1 });
        await store.EnqueueAsync("events/Subscriptions/b", new byte[] { 2 });

        (await purger.PurgeSubscriptionAsync("events", "a")).ShouldBe(1L);
        (await store.CountAsync("events/Subscriptions/a")).ShouldBe(0L);
        (await store.CountAsync("events/Subscriptions/b")).ShouldBe(1L);
    }

    [Fact]
    public async Task PurgeAll_CoversQueuesSubscriptionsAndDlqs_AndCountsEntitiesOnce()
    {
        var (purger, topics, queues, store) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "orders" });
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "a" });
        await store.EnqueueAsync("orders", new byte[] { 1 });
        await store.EnqueueAsync("orders/$DeadLetterQueue", new byte[] { 2 });
        await store.EnqueueAsync("events/Subscriptions/a", new byte[] { 3 });

        var (purged, entities) = await purger.PurgeAllAsync();

        purged.ShouldBe(3L);
        entities.ShouldBe(2);
        (await store.CountAsync("orders")).ShouldBe(0L);
        (await store.CountAsync("orders/$DeadLetterQueue")).ShouldBe(0L);
        (await store.CountAsync("events/Subscriptions/a")).ShouldBe(0L);
    }

    [Fact]
    public async Task Purge_UnknownEntities_ReturnNull()
    {
        var (purger, _, _, _) = NewFixture();
        (await purger.PurgeQueueAsync("nope")).ShouldBeNull();
        (await purger.PurgeTopicAsync("nope")).ShouldBeNull();
        (await purger.PurgeSubscriptionAsync("nope", "sub")).ShouldBeNull();
    }
}
