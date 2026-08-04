using OpenServiceBus.Amqp.DeadLettering;

using System.Diagnostics;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Transactions;
using Amqp.Types;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Diagnostics;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Core.Transactions;

namespace OpenServiceBus.Amqp.Queues;

/// <summary>
/// Implements the broker-side of a receive link: pulls the next available message from
/// the store under peek-lock, stamps Service Bus system properties, translates client
/// dispositions back into store operations, moves messages to the DLQ when an explicit
/// Rejected disposition arrives or when the delivery-count budget is exhausted, and
/// dead-letters / drops expired messages on dequeue.
/// One instance per (queue, sub-resource) - multiple client receivers can share it safely.
/// </summary>
public sealed class QueueReceiverSource : IMessageSource
{
    // Annotation symbols match Azure.Messaging.ServiceBus's AmqpMessageConstants exactly.
    private static readonly Symbol EnqueuedTimeUtcSymbol = new("x-opt-enqueued-time");
    private static readonly Symbol SequenceNumberSymbol = new("x-opt-sequence-number");
    private static readonly Symbol LockedUntilSymbol = new("x-opt-locked-until");

    // Same names appear as Symbol keys on Rejected.Error.Info (the SDK's preferred shape).
    private static readonly Symbol DeadLetterReasonSymbol = new(DeadLetterEncoder.DeadLetterReasonHeader);
    private static readonly Symbol DeadLetterErrorDescriptionSymbol = new(DeadLetterEncoder.DeadLetterErrorDescriptionHeader);

    // Default reason when the SDK calls DeadLetterMessageAsync(msg) with no reason supplied.
    private const string MaxDeliveryReason = "MaxDeliveryCountExceeded";

    // Standard Service Bus reasons for TTL expiration.
    public const string TtlExpiredReason = "TTLExpiredException";
    public const string TtlExpiredDescription = "The message expired and was moved to the dead-letter queue.";

    private readonly string _entityName;
    private readonly QueueDescriptor _descriptor;
    private readonly IMessageStore _store;
    private readonly IMessageRouter _router;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueReceiverSource> _logger;
    private readonly bool _isDlq;
    private readonly IQueueRegistry? _registry;

    public QueueReceiverSource(
        string entityName,
        QueueDescriptor descriptor,
        IMessageStore store,
        IMessageRouter router,
        ITransactionManager transactions,
        TimeProvider timeProvider,
        ILogger<QueueReceiverSource> logger,
        IQueueRegistry? registry = null)
    {
        _entityName = entityName;
        _descriptor = descriptor;
        _store = store;
        _router = router;
        _transactions = transactions;
        _timeProvider = timeProvider;
        _logger = logger;
        _registry = registry;
        _isDlq = EntityNames.IsDeadLetterQueue(entityName);
    }

    public async Task<ReceiveContext> GetMessageAsync(ListenerLink link)
    {
        while (true)
        {
            // The SDK drains the link before closing any receiver/processor. For drain to
            // complete, GetMessageAsync MUST return null so SourceLinkEndpoint can call
            // link.CompleteDrain() - a dequeue that blocks indefinitely makes every graceful
            // receiver close hang for the client's full 60s timeout instead. Poll on a short
            // timeout so link.IsDraining is observed periodically (same pattern as
            // SessionReceiverSource.GetMessageAsync).
            LockedMessage? locked = null;
            while (locked is null)
            {
                if (link.IsDraining) return null!;
                // A receive-disabled entity delivers nothing: credit stays outstanding and the
                // link parks here until the status flips back (or the link drains/closes).
                if (_registry?.GetAsync(_entityName).GetAwaiter().GetResult() is { } current
                    && !current.Status.AcceptsReceives())
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    continue;
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                try
                {
                    // Pass the receiver link name so the lock is scoped to this link - only this
                    // link's $management session can renew it (matches Service Bus's lock-link affinity).
                    locked = await _store.TryDequeueAsync(_entityName, _descriptor.LockDuration, link.Name, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Poll timeout - loop and re-check drain state.
                }

                if (locked is null && !cts.IsCancellationRequested)
                {
                    // Store-side cancellation (e.g. queue deleted) - bail out like before.
                    return null!;
                }
            }

            // Messages that crossed their TTL deadline while waiting in the queue get
            // dropped (or moved to DLQ when DeadLetteringOnMessageExpiration is set on the queue).
            if (locked.Message.IsExpired(_timeProvider.GetUtcNow()))
            {
                await HandleExpiredOnDequeueAsync(locked.LockToken).ConfigureAwait(false);
                continue;
            }

            // If the wire delivery-count has reached MaxDeliveryCount, skip delivery and move to DLQ.
            // The DLQ itself never auto-dead-letters (MaxDeliveryCount = int.MaxValue and _isDlq guards anyway).
            if (!_isDlq && locked.Message.DeliveryCount >= _descriptor.MaxDeliveryCount)
            {
                await DeadLetterAsync(
                    locked.LockToken,
                    MaxDeliveryReason,
                    $"Message could not be consumed within {_descriptor.MaxDeliveryCount} delivery attempts.")
                    .ConfigureAwait(false);
                continue;
            }

            var amqp = DecodeMessage(locked.Message.EncodedMessage);
            StampSystemProperties(amqp, locked);

            // Emit a receive activity carrying the messaging conventions. Cheap when no
            // listener is attached (StartActivity returns null and the SetTag block is skipped).
            using (var activity = OpenServiceBusDiagnostics.ActivitySource.StartActivity(
                OpenServiceBusDiagnostics.SpanReceive, ActivityKind.Consumer))
            {
                if (activity is not null)
                {
                    activity.SetTag(OpenServiceBusDiagnostics.TagSystem, OpenServiceBusDiagnostics.SystemValue);
                    activity.SetTag(OpenServiceBusDiagnostics.TagDestination, _entityName);
                    activity.SetTag(OpenServiceBusDiagnostics.TagOperation, "receive");
                    activity.SetTag(OpenServiceBusDiagnostics.TagDeliveryCount, locked.Message.DeliveryCount);
                    if (amqp.Properties?.MessageId is { } mid)
                        activity.SetTag(OpenServiceBusDiagnostics.TagMessageId, mid.ToString());
                    if (locked.Message.SessionId is { } sid)
                        activity.SetTag(OpenServiceBusDiagnostics.TagSessionId, sid);
                }
            }
            OpenServiceBusDiagnostics.MessagesReceived.Add(1,
                new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDestination, _entityName));

            // ReceiveAndDelete: the client opened the link with snd-settle-mode=settled, meaning
            // we send the message as settled and NO disposition will arrive. The peek-lock we just
            // took would otherwise expire → ghost redelivery. Settle (delete) it now so the message
            // is gone the moment we hand it to the framework. This is at-most-once semantics - a
            // wire-level send failure loses the message, matching real Service Bus.
            if (link.SettleOnSend)
            {
                await _store.TryCompleteAsync(_entityName, locked.LockToken).ConfigureAwait(false);
            }

            var ctx = new ReceiveContext(link, amqp) { UserToken = locked.LockToken };
            return ctx;
        }
    }

    public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext)
    {
        if (receiveContext.UserToken is not Guid lockToken)
        {
            dispositionContext.Complete();
            return;
        }

        // Settle activity covers the whole disposition pipeline. We tag the outcome at the
        // end (post-switch) so transactional and non-transactional paths both produce one span.
        using var activity = OpenServiceBusDiagnostics.ActivitySource.StartActivity(
            OpenServiceBusDiagnostics.SpanSettle, ActivityKind.Consumer);
        if (activity is not null)
        {
            activity.SetTag(OpenServiceBusDiagnostics.TagSystem, OpenServiceBusDiagnostics.SystemValue);
            activity.SetTag(OpenServiceBusDiagnostics.TagDestination, _entityName);
            activity.SetTag(OpenServiceBusDiagnostics.TagOperation, "settle");
        }
        string disposition;

        try
        {
            // A transactional disposition wraps the real outcome in TransactionalState.
            // Buffer the store op under the txn and reply with a transactional Accepted;
            // commit/rollback happens via the coordinator's discharge.
            if (dispositionContext.DeliveryState is TransactionalState txnState && txnState.TxnId is { Length: > 0 } txnId)
            {
                var inner = txnState.Outcome;
                var enlisted = _transactions.Enlist(txnId, _ => InvokeDispositionAsync(lockToken, inner));
                if (enlisted)
                {
                    receiveContext.Link.DisposeMessage(receiveContext.Message,
                        new TransactionalState { TxnId = txnId, Outcome = new Accepted() }, settled: true);
                }
                else
                {
                    dispositionContext.Complete(new Error(new Symbol(ErrorCode.IllegalState))
                    {
                        Description = "Unknown or already-discharged transaction id.",
                    });
                }
                activity?.SetTag(OpenServiceBusDiagnostics.TagDisposition, "transactional");
                return;
            }

            switch (dispositionContext.DeliveryState)
            {
                case Accepted:
                    _store.TryCompleteAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    disposition = "complete";
                    break;

                // Modified with UndeliverableHere=true is the SDK's DeferAsync wire signal -
                // park the message in Deferred state instead of returning it to the active pool.
                case Modified modified when modified.UndeliverableHere:
                    _store.TryDeferAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    disposition = "defer";
                    break;

                case Released:
                case Modified:
                    _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    disposition = "abandon";
                    break;

                case Rejected rejected:
                    var (reason, description) = ExtractDeadLetterInfo(rejected);
                    DeadLetterAsync(lockToken, reason, description, inFlightDelivery: true).GetAwaiter().GetResult();
                    disposition = "deadletter";
                    if (reason is not null) activity?.SetTag(OpenServiceBusDiagnostics.TagDeadLetterReason, reason);
                    break;

                default:
                    _logger.LogWarning(
                        "Unexpected delivery state {State} for lock {Lock} on {Entity}",
                        dispositionContext.DeliveryState?.GetType().Name ?? "<null>", lockToken, _entityName);
                    _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    disposition = "abandon";
                    break;
            }

            // Tag + count once the outcome is known. Counters carry the disposition status so
            // dashboards can break down complete-vs-dlq-vs-abandon rates per destination.
            activity?.SetTag(OpenServiceBusDiagnostics.TagDisposition, disposition);
            OpenServiceBusDiagnostics.MessagesDispositioned.Add(1,
                new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDestination, _entityName),
                new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDisposition, disposition));
        }
        finally
        {
            // Only complete here for the non-tx path; the tx branch already settled the delivery
            // with a TransactionalState outcome above.
            if (dispositionContext.DeliveryState is not TransactionalState)
            {
                dispositionContext.Complete();
            }
        }
    }

    /// <summary>
    /// Apply a single buffered disposition outcome against the store. Runs at txn commit time
    /// inside <see cref="ITransactionManager.CommitAsync"/>.
    /// </summary>
    private async Task InvokeDispositionAsync(Guid lockToken, Outcome? outcome)
    {
        switch (outcome)
        {
            case Accepted:
                await _store.TryCompleteAsync(_entityName, lockToken).ConfigureAwait(false);
                break;
            case Modified modified when modified.UndeliverableHere:
                await _store.TryDeferAsync(_entityName, lockToken).ConfigureAwait(false);
                break;
            case Released:
            case Modified:
                await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
                break;
            case Rejected rejected:
                var (reason, description) = ExtractDeadLetterInfo(rejected);
                await DeadLetterAsync(lockToken, reason, description, inFlightDelivery: true).ConfigureAwait(false);
                break;
            default:
                // Treat unknown as abandon so the lock is at least released on commit.
                await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// A message was dequeued under peek-lock but its TTL has already passed. Either dead-letter it
    /// (when the queue is configured to) or drop it. Either way the message is gone from the source queue.
    /// </summary>
    private async Task HandleExpiredOnDequeueAsync(Guid lockToken, CancellationToken cancellationToken = default)
    {
        if (!_isDlq && _descriptor.DeadLetteringOnMessageExpiration)
        {
            await DeadLetterAsync(lockToken, TtlExpiredReason, TtlExpiredDescription, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        // Drop: just remove the locked message; no DLQ enqueue.
        await _store.TryRemoveLockedAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("TTL-dropped expired message from {Entity}", _entityName);
    }

    private async Task DeadLetterAsync(Guid lockToken, string? reason, string? description, bool inFlightDelivery = false, CancellationToken cancellationToken = default)
    {
        if (_isDlq)
        {
            // No DLQ for the DLQ - just release the lock without enqueue elsewhere.
            await _store.TryAbandonAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
            return;
        }

        var removed = await _store.TryRemoveLockedAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
        if (removed is null) return;

        var dlqBytes = DeadLetterEncoder.AppendDeadLetterHeaders(removed.EncodedMessage, _entityName, reason, description);
        // If the entity is configured to forward dead-lettered messages elsewhere, route there;
        // otherwise the message lands on the local <entity>/$DeadLetterQueue as before.
        var dlqTarget = string.IsNullOrEmpty(_descriptor.ForwardDeadLetteredMessagesTo)
            ? _entityName + EntityNames.DeadLetterSuffix
            : _descriptor.ForwardDeadLetteredMessagesTo!;
        // The moved message keeps its delivery history - Azure SB shows e.g. DeliveryCount=10
        // on a max-delivery dead-lettered message, not 0. The stored count only tracks *prior*
        // failed deliveries, so a client dead-lettering the delivery it currently holds counts
        // that delivery too (inFlightDelivery); broker-initiated moves (max-delivery skip, TTL)
        // pass the stored count as-is.
        await _router.RouteAsync(dlqTarget, dlqBytes, expiresAt: null,
            deliveryCount: removed.DeliveryCount + (inFlightDelivery ? 1 : 0),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Dead-letter counter tagged with the source queue and reason - lets a dashboard
        // group "DLQ rate per queue" and spot reason-specific spikes (MaxDeliveryCountExceeded vs TTL).
        OpenServiceBusDiagnostics.MessagesDeadLettered.Add(1,
            new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDeadLetterSource, _entityName),
            new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDeadLetterReason, reason ?? "(unspecified)"));

        _logger.LogDebug("Dead-lettered seq#{Seq} from {Entity} to {Dlq} (reason={Reason})",
            removed.SequenceNumber, _entityName, dlqTarget, reason ?? "(unspecified)");
    }

    private static (string? Reason, string? Description) ExtractDeadLetterInfo(Rejected rejected)
    {
        if (rejected.Error?.Info is null) return (null, null);
        rejected.Error.Info.TryGetValue(DeadLetterReasonSymbol, out var reason);
        rejected.Error.Info.TryGetValue(DeadLetterErrorDescriptionSymbol, out var description);
        return (reason as string, description as string);
    }

    private static void StampSystemProperties(Message amqp, LockedMessage locked)
    {
        amqp.Header ??= new Header();
        amqp.Header.DeliveryCount = (uint)locked.Message.DeliveryCount;

        amqp.MessageAnnotations ??= new MessageAnnotations();
        amqp.MessageAnnotations.Map[SequenceNumberSymbol] = locked.Message.SequenceNumber;
        amqp.MessageAnnotations.Map[EnqueuedTimeUtcSymbol] = locked.Message.EnqueuedAt.UtcDateTime;
        amqp.MessageAnnotations.Map[LockedUntilSymbol] = locked.LockedUntil.UtcDateTime;
    }

    private static Message DecodeMessage(byte[] encoded)
    {
        var buffer = new ByteBuffer(encoded, 0, encoded.Length, encoded.Length);
        return Message.Decode(buffer);
    }
}
