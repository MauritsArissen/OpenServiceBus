using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Transactions;
using Amqp.Types;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Amqp.DeadLettering;
using OpenServiceBus.Amqp.Settlement;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Core.Transactions;

namespace OpenServiceBus.Amqp.Queues;

/// <summary>
/// Session-aware variant of <see cref="QueueReceiverSource"/>. One instance per receiver link,
/// scoped to a specific <see cref="SessionId"/>. Delivers only messages belonging to that
/// session and assumes the link's session lock has already been acquired by the
/// <c>EntityLinkProcessor</c> at attach time (so the SDK's <c>AcceptSessionAsync</c> /
/// <c>AcceptNextSessionAsync</c> calls resolve before any first delivery).
/// </summary>
public sealed class SessionReceiverSource : IMessageSource
{
    private static readonly Symbol EnqueuedTimeUtcSymbol = new("x-opt-enqueued-time");
    private static readonly Symbol SequenceNumberSymbol = new("x-opt-sequence-number");
    private static readonly Symbol EnqueuedSequenceNumberSymbol = new("x-opt-enqueue-sequence-number");
    private static readonly Symbol LockedUntilSymbol = new("x-opt-locked-until");
    private static readonly Symbol MessageStateSymbol = new("x-opt-message-state");

    // ServiceBusMessageState.Active - see QueueReceiverSource.
    private const int MessageStateActive = 0;
    private static readonly Symbol DeadLetterReasonSymbol = new(DeadLetterEncoder.DeadLetterReasonHeader);
    private static readonly Symbol DeadLetterErrorDescriptionSymbol = new(DeadLetterEncoder.DeadLetterErrorDescriptionHeader);

    public const string TtlExpiredReason = "TTLExpiredException";
    public const string TtlExpiredDescription = "The message expired and was moved to the dead-letter queue.";
    private const string MaxDeliveryReason = "MaxDeliveryCountExceeded";

    private readonly string _entityName;
    private readonly QueueDescriptor _descriptor;
    private readonly IMessageStore _store;
    private readonly IMessageRouter _router;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionReceiverSource> _logger;
    private readonly bool _isDlq;
    private readonly IQueueRegistry? _registry;
    private readonly EntityActivityTracker? _activity;

    public string SessionId { get; }
    public string? LinkName { get; }

    private volatile bool _pumpStarted;

    /// <summary>True once the client has issued credit and the receive pump has run at
    /// least once - the liveness signal EntityLinkProcessor's zombie watchdog checks for
    /// parked accept-next-session completions.</summary>
    public bool PumpStarted => _pumpStarted;

    public SessionReceiverSource(
        string entityName,
        QueueDescriptor descriptor,
        IMessageStore store,
        IMessageRouter router,
        ITransactionManager transactions,
        TimeProvider timeProvider,
        ILogger<SessionReceiverSource> logger,
        string sessionId,
        string? linkName,
        IQueueRegistry? registry = null,
        EntityActivityTracker? activityTracker = null)
    {
        _registry = registry;
        _activity = activityTracker;
        _entityName = entityName;
        _descriptor = descriptor;
        _store = store;
        _router = router;
        _transactions = transactions;
        _timeProvider = timeProvider;
        _logger = logger;
        _isDlq = EntityNames.IsDeadLetterQueue(entityName);
        SessionId = sessionId;
        LinkName = linkName;
    }

    public async Task<ReceiveContext> GetMessageAsync(ListenerLink link)
    {
        _pumpStarted = true;
        while (true)
        {
            // The SDK's session receiver calls DrainAsync after every receive (see decompiled
            // AmqpReceiver.ReceiveMessagesAsyncInternal line 2762). For drain to complete,
            // GetMessageAsync MUST return null so SourceLinkEndpoint can call link.CompleteDrain().
            // Poll the channel on a short timeout so we periodically observe link.IsDraining /
            // IsDetaching and yield null when either is set.
            LockedMessage? locked = null;
            while (locked is null)
            {
                if (link.IsDraining) return null!;
                // See the matching note in QueueReceiverSource: a closed link's in-flight poll
                // must exit, or it lives on as a zombie consumer stealing the session's messages.
                if (link.IsClosed) return null!;
                if (_registry is not null)
                {
                    var current = _registry.GetAsync(_entityName).GetAwaiter().GetResult();
                    if (current is null)
                    {
                        Routing.DeletedEntityLink.Close(link, _entityName, _logger);
                        return null!;
                    }
                    if (!current.Status.AcceptsReceives())
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                        continue;
                    }
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                try
                {
                    locked = await _store.TryDequeueFromSessionAsync(
                        _entityName, SessionId, _descriptor.LockDuration, link.Name, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Poll timeout - loop and re-check drain state.
                }
                catch (InvalidOperationException)
                {
                    Routing.DeletedEntityLink.Close(link, _entityName, _logger);
                    return null!;
                }

                if (locked is null && !cts.IsCancellationRequested)
                {
                    // The session lock was lost (expired or taken over) - tell the client with a
                    // proper detach instead of a silent pump stop, so the SDK's session receiver
                    // surfaces SessionLockLost rather than an inexplicable receive timeout.
                    _logger.LogDebug("Session lock lost for '{Session}' on '{Entity}'; detaching receiver.", SessionId, _entityName);
                    CloseSessionLockLost(link);
                    return null!;
                }
            }

            if (link.IsClosed)
            {
                await _store.TryAbandonAsync(_entityName, locked.LockToken).ConfigureAwait(false);
                return null!;
            }

            _activity?.Touch(_entityName);

            if (locked.Message.IsExpired(_timeProvider.GetUtcNow()))
            {
                await HandleExpiredOnDequeueAsync(locked.LockToken).ConfigureAwait(false);
                continue;
            }

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

            if (link.SettleOnSend)
            {
                await _store.TryCompleteAsync(_entityName, locked.LockToken).ConfigureAwait(false);
            }

            return new ReceiveContext(link, amqp) { UserToken = locked.LockToken };
        }
    }

    public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext)
    {
        if (receiveContext.UserToken is not Guid lockToken)
        {
            dispositionContext.Complete();
            return;
        }

        DeliveryState? failureOutcome = null;
        try
        {
            // Transactional disposition - buffer the store op under the txn.
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
                    // Reject unknown/discharged txns instead of settling as success - see the
                    // matching note in QueueReceiverSource; a false success silently loses the
                    // disposition and lets the message redeliver.
                    dispositionContext.Complete(new Error(new Symbol(ErrorCode.IllegalState))
                    {
                        Description = "Unknown or already-discharged transaction id.",
                    });
                }
                return;
            }

            bool settled;
            switch (dispositionContext.DeliveryState)
            {
                case Accepted:
                    settled = _store.TryCompleteAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Modified modified when modified.UndeliverableHere:
                    ApplyPropertiesToModifyAsync(lockToken, PropertiesToModifyCodec.FromModified(modified)).GetAwaiter().GetResult();
                    settled = _store.TryDeferAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Modified modified:
                    ApplyPropertiesToModifyAsync(lockToken, PropertiesToModifyCodec.FromModified(modified)).GetAwaiter().GetResult();
                    settled = _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Released:
                    settled = _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Rejected rejected:
                    var (reason, description, dlqProps) = PropertiesToModifyCodec.FromRejected(rejected);
                    settled = DeadLetterAsync(lockToken, reason, description, dlqProps, inFlightDelivery: true).GetAwaiter().GetResult();
                    break;
                default:
                    _logger.LogWarning(
                        "Unexpected delivery state {State} for lock {Lock} on session {Session}",
                        dispositionContext.DeliveryState?.GetType().Name ?? "<null>", lockToken, SessionId);
                    settled = _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
            }

            if (!settled)
            {
                failureOutcome = LockLostFailureOutcome();
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Ignored disposition for lock {Lock} on deleted entity {Entity}", lockToken, _entityName);
        }
        finally
        {
            // Echo a fresh copy of the receiver's outcome when settling - see the matching
            // note in QueueReceiverSource: proton-j verifies the remote state matches what
            // it sent, and its received state instances cannot be re-encoded. A refused
            // settle is Rejected with message-lock-lost, or session-lock-lost when the
            // whole session lock is gone rather than just the message lock.
            if (dispositionContext.DeliveryState is not TransactionalState)
            {
                dispositionContext.Link.DisposeMessage(
                    dispositionContext.Message,
                    failureOutcome ?? EchoOutcome(dispositionContext.DeliveryState),
                    settled: true);
            }
        }
    }

    // The message lock and the session lock fail independently: settling after only the
    // message lock expired is MessageLockLost (the session is still ours), while settling
    // after the session lock was lost/taken over is SessionLockLost - matching Azure.
    private DeliveryState LockLostFailureOutcome() =>
        _store.IsSessionLockHeldAsync(_entityName, SessionId, LinkName).GetAwaiter().GetResult()
            ? LockLostOutcome.Message(_entityName)
            : LockLostOutcome.Session(SessionId);

    private async Task InvokeDispositionAsync(Guid lockToken, Outcome? outcome)
    {
        bool settled;
        try
        {
            switch (outcome)
            {
                case Accepted:
                    settled = await _store.TryCompleteAsync(_entityName, lockToken).ConfigureAwait(false);
                    break;
                case Modified modified when modified.UndeliverableHere:
                    await ApplyPropertiesToModifyAsync(lockToken, PropertiesToModifyCodec.FromModified(modified)).ConfigureAwait(false);
                    settled = await _store.TryDeferAsync(_entityName, lockToken).ConfigureAwait(false);
                    break;
                case Modified modified:
                    await ApplyPropertiesToModifyAsync(lockToken, PropertiesToModifyCodec.FromModified(modified)).ConfigureAwait(false);
                    settled = await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
                    break;
                case Released:
                    settled = await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
                    break;
                case Rejected rejected:
                    var (reason, description, dlqProps) = PropertiesToModifyCodec.FromRejected(rejected);
                    settled = await DeadLetterAsync(lockToken, reason, description, dlqProps, inFlightDelivery: true).ConfigureAwait(false);
                    break;
                default:
                    settled = await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Ignored transactional disposition for lock {Lock} on deleted entity {Entity}", lockToken, _entityName);
            return;
        }

        // See the matching note in QueueReceiverSource: fail the discharge on a lost lock.
        if (!settled)
        {
            throw new LockLostException(
                $"The lock for token {lockToken} on session '{SessionId}' of '{_entityName}' was lost before the transaction committed.");
        }
    }

    // See the matching note in QueueReceiverSource: merge while the lock is held so the
    // settle re-exposes the modified payload; a lost lock makes this a no-op.
    private async Task ApplyPropertiesToModifyAsync(Guid lockToken, IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0) return;
        var locked = await _store.TryGetLockedAsync(_entityName, lockToken).ConfigureAwait(false);
        if (locked is null) return;
        var merged = PropertiesToModifyCodec.MergeIntoEncoded(locked.EncodedMessage, properties);
        await _store.TryUpdateLockedPayloadAsync(_entityName, lockToken, merged).ConfigureAwait(false);
    }

    private async Task HandleExpiredOnDequeueAsync(Guid lockToken, CancellationToken cancellationToken = default)
    {
        if (!_isDlq && _descriptor.DeadLetteringOnMessageExpiration)
        {
            await DeadLetterAsync(lockToken, TtlExpiredReason, TtlExpiredDescription, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        await _store.TryRemoveLockedAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DeadLetterAsync(
        Guid lockToken,
        string? reason,
        string? description,
        IReadOnlyDictionary<string, object?>? propertiesToModify = null,
        bool inFlightDelivery = false,
        CancellationToken cancellationToken = default)
    {
        if (_isDlq)
        {
            return await _store.TryAbandonAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
        }
        var removed = await _store.TryRemoveLockedAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
        if (removed is null) return false;
        var dlqBytes = DeadLetterEncoder.AppendDeadLetterHeaders(removed.EncodedMessage, _entityName, reason, description, propertiesToModify);
        // Honor ForwardDeadLetteredMessagesTo, falling back to the local DLQ.
        var dlqTarget = string.IsNullOrEmpty(_descriptor.ForwardDeadLetteredMessagesTo)
            ? _entityName + EntityNames.DeadLetterSuffix
            : _descriptor.ForwardDeadLetteredMessagesTo!;
        // See QueueReceiverSource.DeadLetterAsync for the delivery-count semantics.
        await _router.RouteAsync(dlqTarget, dlqBytes, expiresAt: null,
            deliveryCount: removed.DeliveryCount + (inFlightDelivery ? 1 : 0),
            forwardSource: string.IsNullOrEmpty(_descriptor.ForwardDeadLetteredMessagesTo) ? null : _entityName,
            enqueuedSequenceNumber: removed.EnqueuedSequenceNumber,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void CloseSessionLockLost(ListenerLink link)
    {
        if (link.IsClosed) return;
        try
        {
            link.Close(TimeSpan.Zero, new Error(new Symbol(Routing.ServiceBusErrors.SessionLockLost))
            {
                Info = new Fields(),
                Description = $"The session lock for session '{SessionId}' on '{_entityName}' was lost. Accept the session again to continue.",
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detach session receiver for '{Session}' on '{Entity}'", SessionId, _entityName);
        }
    }

    private static DeliveryState EchoOutcome(DeliveryState received) => received switch
    {
        Rejected => new Rejected(),
        Released => new Released(),
        Modified m => new Modified { DeliveryFailed = m.DeliveryFailed, UndeliverableHere = m.UndeliverableHere },
        _ => new Accepted(),
    };

    private static void StampSystemProperties(Message amqp, LockedMessage locked)
    {
        amqp.Header ??= new Header();
        amqp.Header.DeliveryCount = (uint)locked.Message.DeliveryCount;

        amqp.MessageAnnotations ??= new MessageAnnotations();
        amqp.MessageAnnotations.Map[SequenceNumberSymbol] = locked.Message.SequenceNumber;
        amqp.MessageAnnotations.Map[EnqueuedSequenceNumberSymbol] = locked.Message.EnqueuedSequenceNumber;
        amqp.MessageAnnotations.Map[EnqueuedTimeUtcSymbol] = locked.Message.EnqueuedAt.UtcDateTime;
        amqp.MessageAnnotations.Map[LockedUntilSymbol] = locked.LockedUntil.UtcDateTime;
        amqp.MessageAnnotations.Map[MessageStateSymbol] = MessageStateActive;
    }

    private static Message DecodeMessage(byte[] encoded)
    {
        var buffer = new ByteBuffer(encoded, 0, encoded.Length, encoded.Length);
        return Message.Decode(buffer);
    }
}
