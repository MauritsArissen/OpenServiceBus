using OpenServiceBus.Amqp.DeadLettering;
using OpenServiceBus.Amqp.Queues;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Diagnostics;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Amqp.Lifecycle;

/// <summary>
/// Periodic sweeper that removes (or dead-letters) messages past their TTL deadline.
/// Works alongside the dequeue-time check in <see cref="QueueReceiverSource"/> so that
/// idle queues with expired messages don't accumulate junk just because no consumer is reading.
/// Each tick is a single store-wide call (<see cref="IMessageStore.ExpireMessagesAll"/>) so
/// the sweep cost does not grow with the number of entities, and the timer backs off to
/// <see cref="IdleSweepInterval"/> while nothing in the store carries a TTL
/// (see docs/Background-Sweepers.md).
/// </summary>
public sealed class TtlExpirationService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(2);

    private readonly IMessageStore _store;
    private readonly IQueueRegistry _registry;
    private readonly IMessageRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TtlExpirationService> _logger;

    public TtlExpirationService(
        IMessageStore store,
        IQueueRegistry registry,
        IMessageRouter router,
        TimeProvider timeProvider,
        ILogger<TtlExpirationService> logger)
    {
        _store = store;
        _registry = registry;
        _router = router;
        _timeProvider = timeProvider;
        _logger = logger;
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
        StoreSweep<IReadOnlyList<StoredMessage>> sweep;
        try
        {
            sweep = _store.ExpireMessagesAll(now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTL sweep failed");
            return true;
        }

        foreach (var (queueName, expired) in sweep.ByQueue)
        {
            try
            {
                // Count every message the sweeper dropped or routed to DLQ, tagged with the source queue.
                OpenServiceBusDiagnostics.MessagesExpired.Add(expired.Count,
                    new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDestination, queueName));

                // A null descriptor means the store had rows for something the registry does not
                // track as a queue (topic holding queues, a queue deleted mid-sweep) - those have
                // no dead-letter config, so their expired messages are simply dropped.
                var queue = await _registry.GetAsync(queueName, cancellationToken).ConfigureAwait(false);
                var routeToDlq = queue is not null
                    && !EntityNames.IsDeadLetterQueue(queueName)
                    && queue.DeadLetteringOnMessageExpiration;

                if (routeToDlq)
                {
                    // Per-queue ForwardDeadLetteredMessagesTo overrides the local DLQ destination.
                    var dlqTarget = string.IsNullOrEmpty(queue!.ForwardDeadLetteredMessagesTo)
                        ? queueName + EntityNames.DeadLetterSuffix
                        : queue.ForwardDeadLetteredMessagesTo!;
                    foreach (var msg in expired)
                    {
                        var dlqBytes = DeadLetterEncoder.AppendDeadLetterHeaders(
                            msg.EncodedMessage,
                            queueName,
                            QueueReceiverSource.TtlExpiredReason,
                            QueueReceiverSource.TtlExpiredDescription);
                        await _router.RouteAsync(dlqTarget, dlqBytes, expiresAt: null,
                            deliveryCount: msg.DeliveryCount,
                            forwardSource: string.IsNullOrEmpty(queue.ForwardDeadLetteredMessagesTo) ? null : queueName,
                            enqueuedSequenceNumber: msg.EnqueuedSequenceNumber,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    _logger.LogDebug("TTL-expired {Count} message(s) from {Queue} → {Dlq}", expired.Count, queueName, dlqTarget);
                }
                else
                {
                    _logger.LogDebug("TTL-dropped {Count} message(s) from {Queue}", expired.Count, queueName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTL sweep failed for queue {Queue}", queueName);
            }
        }
        return sweep.ByQueue.Count > 0 || sweep.HasPending;
    }
}
