using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Amqp.Lifecycle;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.InMemoryStorage;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.Amqp.Tests;

public class IdleEntityReaperTests
{
    private static (IdleEntityReaper Reaper, QueueManager Queues, TopicManager Topics, EntityActivityTracker Activity, FakeTimeProvider Clock) Fixture()
    {
        var clock = new FakeTimeProvider();
        var queues = new QueueManager(new InMemoryMessageStore(clock));
        var topics = new TopicManager(queues);
        var activity = new EntityActivityTracker(clock);
        var reaper = new IdleEntityReaper(queues, activity, clock, NullLogger<IdleEntityReaper>.Instance, topics);
        return (reaper, queues, topics, activity, clock);
    }

    [Fact]
    public async Task Sweep_IdleWindowElapsedSinceCreation_DeletesQueueAndItsDlq()
    {
        var (reaper, queues, _, activity, clock) = Fixture();
        await reaper.StartAsync(CancellationToken.None);
        await queues.CreateAsync(new QueueDescriptor { Name = "ephemeral", AutoDeleteOnIdle = TimeSpan.FromMinutes(10) });

        clock.Advance(TimeSpan.FromMinutes(11));
        await reaper.SweepOnceAsync(CancellationToken.None);

        (await queues.GetAsync("ephemeral")).ShouldBeNull();
        (await queues.GetAsync("ephemeral/$DeadLetterQueue")).ShouldBeNull();
        activity.LastActivity("ephemeral").ShouldBeNull();
        await reaper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_ActivityResetsTheIdleClock()
    {
        var (reaper, queues, _, activity, clock) = Fixture();
        await reaper.StartAsync(CancellationToken.None);
        await queues.CreateAsync(new QueueDescriptor { Name = "busy", AutoDeleteOnIdle = TimeSpan.FromMinutes(10) });

        clock.Advance(TimeSpan.FromMinutes(6));
        activity.Touch("busy");
        clock.Advance(TimeSpan.FromMinutes(6));
        await reaper.SweepOnceAsync(CancellationToken.None);
        (await queues.GetAsync("busy")).ShouldNotBeNull("only 6 minutes idle since the last activity");

        clock.Advance(TimeSpan.FromMinutes(5));
        await reaper.SweepOnceAsync(CancellationToken.None);
        (await queues.GetAsync("busy")).ShouldBeNull("11 minutes idle since the last activity");
        await reaper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_QueueWithoutAutoDelete_IsNeverTouched()
    {
        var (reaper, queues, _, _, clock) = Fixture();
        await reaper.StartAsync(CancellationToken.None);
        await queues.CreateAsync(new QueueDescriptor { Name = "forever" });

        clock.Advance(TimeSpan.FromDays(365));
        await reaper.SweepOnceAsync(CancellationToken.None);

        (await queues.GetAsync("forever")).ShouldNotBeNull();
        await reaper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_IdleSubscriptionAndTopic_AreDeleted()
    {
        var (reaper, _, topics, _, clock) = Fixture();
        await reaper.StartAsync(CancellationToken.None);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events", AutoDeleteOnIdle = TimeSpan.FromMinutes(20) });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
        {
            TopicName = "events", Name = "shortlived", AutoDeleteOnIdle = TimeSpan.FromMinutes(10),
        });

        clock.Advance(TimeSpan.FromMinutes(11));
        await reaper.SweepOnceAsync(CancellationToken.None);
        (await topics.GetSubscriptionAsync("events", "shortlived")).ShouldBeNull();
        (await topics.GetTopicAsync("events")).ShouldNotBeNull("the topic's own window has not elapsed");

        clock.Advance(TimeSpan.FromMinutes(10));
        await reaper.SweepOnceAsync(CancellationToken.None);
        (await topics.GetTopicAsync("events")).ShouldBeNull();
        await reaper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Validation_AutoDeleteOnIdleBelowFiveMinutes_IsRejected()
    {
        var queues = new QueueManager(new InMemoryMessageStore());

        Should.Throw<InvalidOperationException>(() =>
                queues.CreateAsync(new QueueDescriptor { Name = "q", AutoDeleteOnIdle = TimeSpan.FromMinutes(4) }))
            .Message.ShouldContain("at least 5 minutes");
    }
}
