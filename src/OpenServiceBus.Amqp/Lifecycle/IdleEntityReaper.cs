using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Amqp.Lifecycle;

/// <summary>
/// Periodic sweeper enforcing <c>AutoDeleteOnIdle</c>: entities whose idle window elapsed
/// since their last recorded activity are deleted (queue with its DLQ and messages,
/// subscription with its backing queue, topic with its subscriptions). Runs on the broker
/// <see cref="TimeProvider"/>, so fake-time tests can time-travel the window. Entities the
/// tracker has never seen (fresh creates, restarts) get their idle clock seeded on the
/// first sweep. Semantics in docs/Auto-Delete-On-Idle.md.
/// </summary>
public sealed class IdleEntityReaper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry? _topics;
    private readonly EntityActivityTracker _activity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IdleEntityReaper> _logger;

    public IdleEntityReaper(
        IQueueRegistry queues,
        EntityActivityTracker activityTracker,
        TimeProvider timeProvider,
        ILogger<IdleEntityReaper> logger,
        ITopicRegistry? topics = null)
    {
        _queues = queues;
        _topics = topics;
        _activity = activityTracker;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Creation seeds the idle clock, so "created and never used" entities age from
        // their creation moment - including under fake time, where no timer has fired yet.
        _queues.QueueCreated += OnEntityCreated;
        if (_topics is not null)
        {
            _topics.TopicCreated += OnTopicCreated;
            _topics.SubscriptionCreated += OnSubscriptionCreated;
        }
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queues.QueueCreated -= OnEntityCreated;
        if (_topics is not null)
        {
            _topics.TopicCreated -= OnTopicCreated;
            _topics.SubscriptionCreated -= OnSubscriptionCreated;
        }
        return base.StopAsync(cancellationToken);
    }

    private void OnEntityCreated(object? sender, QueueDescriptor descriptor) => _activity.Touch(descriptor.Name);

    private void OnTopicCreated(object? sender, TopicDescriptor descriptor) => _activity.Touch(descriptor.Name);

    private void OnSubscriptionCreated(object? sender, SubscriptionDescriptor descriptor) => _activity.Touch(descriptor.BackingQueueName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var queue in await _queues.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (queue.AutoDeleteOnIdle is not { } idle) continue;
            if (EntityNames.IsDeadLetterQueue(queue.Name)) continue;
            if (queue.Name.Contains(EntityNames.SubscriptionsSegment, StringComparison.OrdinalIgnoreCase)) continue;

            var last = _activity.TouchIfUnseen(queue.Name);
            if (now - last < idle) continue;

            _logger.LogInformation("Auto-deleting queue '{Queue}' after {Idle} idle.", queue.Name, idle);
            await _queues.DeleteAsync(queue.Name, cancellationToken).ConfigureAwait(false);
            _activity.Forget(queue.Name);
            _activity.Forget(queue.Name + EntityNames.DeadLetterSuffix);
        }

        if (_topics is null) return;

        foreach (var topic in await _topics.ListTopicsAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var sub in await _topics.ListSubscriptionsAsync(topic.Name, cancellationToken).ConfigureAwait(false))
            {
                if (sub.AutoDeleteOnIdle is not { } subIdle) continue;
                var subLast = _activity.TouchIfUnseen(sub.BackingQueueName);
                if (now - subLast < subIdle) continue;

                _logger.LogInformation("Auto-deleting subscription '{Sub}' after {Idle} idle.", sub.BackingQueueName, subIdle);
                await _topics.DeleteSubscriptionAsync(sub.TopicName, sub.Name, cancellationToken).ConfigureAwait(false);
                _activity.Forget(sub.BackingQueueName);
                _activity.Forget(sub.BackingQueueName + EntityNames.DeadLetterSuffix);
            }

            if (topic.AutoDeleteOnIdle is not { } topicIdle) continue;
            var topicLast = _activity.TouchIfUnseen(topic.Name);
            if (now - topicLast < topicIdle) continue;

            _logger.LogInformation("Auto-deleting topic '{Topic}' after {Idle} idle.", topic.Name, topicIdle);
            await _topics.DeleteTopicAsync(topic.Name, cancellationToken).ConfigureAwait(false);
            _activity.Forget(topic.Name);
        }
    }
}
