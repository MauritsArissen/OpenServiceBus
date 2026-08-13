using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// Store-level topic duplicate detection (issue #29): one atomic check-and-record per
/// publish keyed on the topic name, sliding-window eviction driven by TimeProvider.
/// </summary>
public class TopicDuplicateDetectionTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task CheckTopicDuplicate_FirstSeenIsNotADuplicate_RepeatWithinWindowIs()
    {
        var store = new InMemoryMessageStore(new FakeTimeProvider());

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue();
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue();
    }

    [Fact]
    public async Task CheckTopicDuplicate_AfterTheWindowExpires_TheIdIsFreshAgain()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryMessageStore(clock);

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckTopicDuplicate_ARepeatSlidesTheWindowForward()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryMessageStore(clock);

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(8));
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(8));
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue(
            "the repeat at t+8 must have refreshed the window, so t+16 is still inside it");
    }

    [Fact]
    public async Task CheckTopicDuplicate_EmptyMessageId_IsNeverADuplicate()
    {
        var store = new InMemoryMessageStore(new FakeTimeProvider());

        (await store.CheckTopicDuplicateAsync("events", "", Window)).ShouldBeFalse();
        (await store.CheckTopicDuplicateAsync("events", "", Window)).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckTopicDuplicate_DifferentTopics_TrackIndependently()
    {
        var store = new InMemoryMessageStore(new FakeTimeProvider());

        (await store.CheckTopicDuplicateAsync("events-a", "m-1", Window)).ShouldBeFalse();
        (await store.CheckTopicDuplicateAsync("events-b", "m-1", Window)).ShouldBeFalse(
            "topic dedup history is keyed per topic");
    }

    [Fact]
    public async Task ClearTopicDedupHistory_ForgetsEverySeenId()
    {
        var store = new InMemoryMessageStore(new FakeTimeProvider());

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        await store.ClearTopicDedupHistoryAsync("events");
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteTopic_ClearsItsDedupHistory()
    {
        var store = new InMemoryMessageStore(new FakeTimeProvider());
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues, store);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events", RequiresDuplicateDetection = true });

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        await topics.DeleteTopicAsync("events");

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse(
            "a recreated topic must not inherit the deleted topic's dedup history");
    }

    [Fact]
    public async Task PurgeTopic_ClearsItsDedupHistory()
    {
        var store = new InMemoryMessageStore(new FakeTimeProvider());
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues, store);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events", RequiresDuplicateDetection = true });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "all" });

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        await new EntityPurger(queues, store, topics).PurgeTopicAsync("events");

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse(
            "purge clears dedup bookkeeping along with the messages, same as queues");
    }
}
