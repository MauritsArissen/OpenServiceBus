using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Transactions;
using Amqp.Types;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Amqp.DeadLettering;
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
    private static readonly Symbol LockedUntilSymbol = new("x-opt-locked-until");
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
        IQueueRegistry? registry = null)
    {
        _registry = registry;
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
                if (_registry?.GetAsync(_entityName).GetAwaiter().GetResult() is { } current
                    && !current.Status.AcceptsReceives())
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    continue;
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

                if (locked is null && !cts.IsCancellationRequested)
                {
                    _logger.LogDebug("Session lock lost for '{Session}' on '{Entity}'; ending receiver pump.", SessionId, _entityName);
                    return null!;
                }
            }

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

            switch (dispositionContext.DeliveryState)
            {
                case Accepted:
                    _store.TryCompleteAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Modified modified when modified.UndeliverableHere:
                    _store.TryDeferAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Released:
                case Modified:
                    _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
                case Rejected rejected:
                    var (reason, description) = ExtractDeadLetterInfo(rejected);
                    DeadLetterAsync(lockToken, reason, description, inFlightDelivery: true).GetAwaiter().GetResult();
                    break;
                default:
                    _logger.LogWarning(
                        "Unexpected delivery state {State} for lock {Lock} on session {Session}",
                        dispositionContext.DeliveryState?.GetType().Name ?? "<null>", lockToken, SessionId);
                    _store.TryAbandonAsync(_entityName, lockToken).GetAwaiter().GetResult();
                    break;
            }
        }
        finally
        {
            if (dispositionContext.DeliveryState is not TransactionalState)
            {
                dispositionContext.Complete();
            }
        }
    }

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
                await _store.TryAbandonAsync(_entityName, lockToken).ConfigureAwait(false);
                break;
        }
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

    private async Task DeadLetterAsync(Guid lockToken, string? reason, string? description, bool inFlightDelivery = false, CancellationToken cancellationToken = default)
    {
        if (_isDlq)
        {
            await _store.TryAbandonAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
            return;
        }
        var removed = await _store.TryRemoveLockedAsync(_entityName, lockToken, cancellationToken).ConfigureAwait(false);
        if (removed is null) return;
        var dlqBytes = DeadLetterEncoder.AppendDeadLetterHeaders(removed.EncodedMessage, _entityName, reason, description);
        // Honor ForwardDeadLetteredMessagesTo, falling back to the local DLQ.
        var dlqTarget = string.IsNullOrEmpty(_descriptor.ForwardDeadLetteredMessagesTo)
            ? _entityName + EntityNames.DeadLetterSuffix
            : _descriptor.ForwardDeadLetteredMessagesTo!;
        // See QueueReceiverSource.DeadLetterAsync for the delivery-count semantics.
        await _router.RouteAsync(dlqTarget, dlqBytes, expiresAt: null,
            deliveryCount: removed.DeliveryCount + (inFlightDelivery ? 1 : 0),
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
