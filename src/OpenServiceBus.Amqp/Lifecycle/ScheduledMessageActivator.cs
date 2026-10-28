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
/// Each tick is a single store-wide call (<see cref="IMessageStore.ActivateScheduledAll"/>)
/// covering queues and topic holding queues alike, so the sweep cost does not grow with the
/// number of entities, and the timer backs off to <see cref="IdleSweepInterval"/> while
/// nothing in the store is scheduled (see docs/Background-Sweepers.md).
///
/// Topics get one extra step: an activated topic message has no receiver, so the sweep FANS
/// IT OUT through the router - filters are evaluated at activation time (a subscription
/// created between schedule and activation receives its copy), matching Azure.
/// </summary>
public sealed class ScheduledMessageActivator : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(1);

    private readonly IMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduledMessageActivator> _logger;
    private readonly ITopicRegistry? _topics;
    private readonly IMessageRouter? _router;

    public ScheduledMessageActivator(
        IMessageStore store,
        TimeProvider timeProvider,
        ILogger<ScheduledMessageActivator> logger,
        ITopicRegistry? topics = null,
        IMessageRouter? router = null)
    {
        _store = store;
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
                var busy = await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                var next = busy
                    ? SweepInterval
                    : TimeSpan.FromTicks(Math.Min(timer.Period.Ticks * 2, IdleSweepInterval.Ticks));
                if (next != timer.Period) timer.Period = next;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private async Task<bool> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        StoreSweep<int> sweep;
        try
        {
            sweep = _store.ActivateScheduledAll(now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled-message sweep failed");
            return true;
        }

        foreach (var (entityName, activated) in sweep.ByQueue)
        {
            try
            {
                if (_topics is not null && _router is { } router
                    && await _topics.GetTopicAsync(entityName, cancellationToken).ConfigureAwait(false) is not null)
                {
                    for (var i = 0; i < activated; i++)
                    {
                        await PublishOneDueTopicMessageAsync(router, entityName, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogDebug("Activated {Count} scheduled message(s) on {Queue}", activated, entityName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled-message sweep failed for {Entity}", entityName);
            }
        }
        return sweep.ByQueue.Count > 0 || sweep.HasPending;
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
            enqueuedSequenceNumber: locked.Message.EnqueuedSequenceNumber,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Activated scheduled publish seq#{Seq} on topic {Topic} to {Count} subscriber(s)",
            locked.Message.SequenceNumber, topicName, landed.Count);
        await _store.TryCompleteAsync(topicName, locked.LockToken, cancellationToken).ConfigureAwait(false);
    }
}
