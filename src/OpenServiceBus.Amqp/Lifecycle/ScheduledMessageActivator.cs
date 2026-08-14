using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Amqp.Topics;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Amqp.Lifecycle;

/// <summary>
/// Periodic sweeper that promotes scheduled messages whose <c>ScheduledEnqueueTime</c> has
/// arrived from "scheduled" to "available" in the store. The dequeue side never sees
/// scheduled messages until this service moves them - so idle queues with future-dated
/// messages stay quiescent until their time.
///
/// Topics get the same sweep over their scheduled-publish holding queue, with one extra
/// step: an activated topic message has no receiver, so the sweep FANS IT OUT through the
/// router - filters are evaluated at activation time (a subscription created between
/// schedule and activation receives its copy), matching Azure.
/// </summary>
public sealed class ScheduledMessageActivator : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);

    private readonly IMessageStore _store;
    private readonly IQueueRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduledMessageActivator> _logger;
    private readonly ITopicRegistry? _topics;
    private readonly IMessageRouter? _router;

    public ScheduledMessageActivator(
        IMessageStore store,
        IQueueRegistry registry,
        TimeProvider timeProvider,
        ILogger<ScheduledMessageActivator> logger,
        ITopicRegistry? topics = null,
        IMessageRouter? router = null)
    {
        _store = store;
        _registry = registry;
        _timeProvider = timeProvider;
        _logger = logger;
        _topics = topics;
        _router = router;
    }

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

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var queues = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var queue in queues)
        {
            try
            {
                var activated = _store.ActivateScheduled(queue.Name, now);
                if (activated > 0)
                {
                    _logger.LogDebug("Activated {Count} scheduled message(s) on {Queue}", activated, queue.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled-message sweep failed for queue {Queue}", queue.Name);
            }
        }

        if (_topics is null || _router is not { } router) return;
        foreach (var topic in await _topics.ListTopicsAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var due = _store.ActivateScheduled(topic.Name, now);
                for (var i = 0; i < due; i++)
                {
                    await PublishOneDueTopicMessageAsync(router, topic.Name, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled-publish sweep failed for topic {Topic}", topic.Name);
            }
        }
    }

    private async Task PublishOneDueTopicMessageAsync(IMessageRouter router, string topicName, CancellationToken cancellationToken)
    {
        // Activation just wrote the sequence numbers to the holding queue's available pool,
        // so this dequeue returns immediately; the short timeout is a safety net against a
        // concurrent purge racing the drain.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        var locked = await _store.TryDequeueAsync(topicName, TimeSpan.FromMinutes(1), cancellationToken: cts.Token).ConfigureAwait(false);
        if (locked is null) return;

        // On a routing failure the lock simply expires and the publish retries on a later
        // sweep rather than losing the message - hence complete only after the fan-out.
        // sessionId stays null: the router resolves the per-subscription session id from
        // the filter context, exactly like the live publish path.
        var filterContext = AmqpFilterContext.FromEncoded(locked.Message.EncodedMessage, _timeProvider.GetUtcNow());
        var landed = await router.RouteAsync(
            topicName, locked.Message.EncodedMessage, locked.Message.ExpiresAt,
            scheduledEnqueueTime: null, sessionId: null, filterContext: filterContext,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Activated scheduled publish seq#{Seq} on topic {Topic} to {Count} subscriber(s)",
            locked.Message.SequenceNumber, topicName, landed.Count);
        await _store.TryCompleteAsync(topicName, locked.LockToken, cancellationToken).ConfigureAwait(false);
    }
}
