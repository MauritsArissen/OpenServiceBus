using System.Diagnostics;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Transactions;
using Amqp.Types;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Diagnostics;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Core.Transactions;

namespace OpenServiceBus.Amqp.Queues;

/// <summary>
/// Handles incoming AMQP sender links from clients sending messages into a queue.
/// One processor instance per attach (created by <see cref="EntityLinkProcessor"/>).
/// </summary>
public sealed class QueueSenderProcessor : IMessageProcessor
{
    private static readonly Symbol ScheduledEnqueueTimeSymbol = new("x-opt-scheduled-enqueue-time");

    /// <summary>
    /// Microsoft.Azure.Amqp's batched-message-format marker. When this is set on the outer
    /// transfer, the body is one or more <c>Data</c> sections (a <see cref="DataList"/> in
    /// AMQPNetLite terms) each holding a fully-encoded inner message. Real Service Bus splits
    /// these on the broker side so each inner message gets its own sequence number - we do the same.
    /// </summary>
    private const uint AmqpBatchedMessageFormat = 0x80013700u;

    private readonly string _queueName;
    private readonly QueueDescriptor _descriptor;
    private readonly IQueueRegistry? _registry;
    private readonly IMessageStore _store;
    private readonly IMessageRouter _router;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueSenderProcessor> _logger;

    public QueueSenderProcessor(
        string queueName,
        QueueDescriptor descriptor,
        IMessageStore store,
        IMessageRouter router,
        ITransactionManager transactions,
        TimeProvider timeProvider,
        ILogger<QueueSenderProcessor> logger,
        IQueueRegistry? registry = null)
    {
        _queueName = queueName;
        _descriptor = descriptor;
        _store = store;
        _router = router;
        _transactions = transactions;
        _timeProvider = timeProvider;
        _logger = logger;
        _registry = registry;
    }

    public int Credit => 100;

    public void Process(MessageContext messageContext)
    {
        try
        {
            // Status is re-resolved per transfer so a flip to (Send)Disabled applies to
            // links that attached while the entity was still active.
            var currentStatus = (_registry?.GetAsync(_queueName).GetAwaiter().GetResult() ?? _descriptor).Status;
            if (!currentStatus.AcceptsSends())
            {
                messageContext.Complete(new Error(new Symbol(Routing.ServiceBusErrors.EntityDisabled))
                {
                    Info = new Fields(),
                    Description = $"Entity '{_queueName}' is {currentStatus} and does not accept messages.",
                });
                return;
            }

            var msg = messageContext.Message;

            var current = _registry?.GetAsync(_queueName).GetAwaiter().GetResult() ?? _descriptor;
            var payloadBytes = PayloadSize(msg);
            if (payloadBytes > current.MaxMessageSizeInKilobytes * 1024)
            {
                messageContext.Complete(new Error(new Symbol(Routing.ServiceBusErrors.MessageSizeExceeded))
                {
                    Info = new Fields(),
                    Description = $"Message of {payloadBytes} bytes exceeds the limit of {current.MaxMessageSizeInKilobytes} KB on '{_queueName}'.",
                });
                return;
            }
            var usage = _store.GetSizeInBytes(_queueName) + _store.GetSizeInBytes(_queueName + EntityNames.DeadLetterSuffix);
            if (usage + payloadBytes > current.MaxSizeInMegabytes * 1024 * 1024)
            {
                messageContext.Complete(new Error(new Symbol(Routing.ServiceBusErrors.QuotaExceeded))
                {
                    Info = new Fields(),
                    Description = $"Entity '{_queueName}' has reached its {current.MaxSizeInMegabytes} MB quota.",
                });
                return;
            }

            // Batched envelope from SendMessagesAsync / ServiceBusMessageBatch - split into N individual
            // enqueues so each inner message gets its own sequence number and lifecycle.
            if (msg.Format == AmqpBatchedMessageFormat && msg.BodySection is DataList dataList)
            {
                // A transactional batch sends all inner messages under the same txn.
                var batchTxnId = (messageContext.DeliveryState as TransactionalState)?.TxnId;
                _ = EnqueueBatchAsync(messageContext, dataList, batchTxnId);
                return;
            }

            var encoded = CopyEncoded(msg);
            var expiresAt = ComputeExpiresAt(msg);
            // Clients can schedule via SendMessageAsync (this path) by stamping the
            // x-opt-scheduled-enqueue-time annotation; or via the dedicated ScheduleMessageAsync
            // which goes through $management. Both end up at IMessageStore.EnqueueAsync(..., scheduledEnqueueTime).
            var scheduledFor = ReadScheduledEnqueueTime(msg);
            // AMQP properties.group-id IS Service Bus's SessionId - but only route by it
            // when the queue is session-enabled; on a plain queue the GroupId is preserved as
            // metadata via the encoded bytes and the message stays on the regular delivery path.
            var sessionId = _descriptor.RequiresSession ? msg.Properties?.GroupId : null;
            // Azure rejects session-less sends to session-enabled entities with amqp:not-allowed
            // (the SDK surfaces InvalidOperationException). Accepting one here would route it to
            // the sessionless channel where no session receiver can ever read it - a black hole.
            if (_descriptor.RequiresSession && string.IsNullOrEmpty(sessionId))
            {
                messageContext.Complete(new Error(new Symbol(ErrorCode.NotAllowed))
                {
                    Description = $"A SessionId is required for messages sent to session-enabled entity '{_queueName}'.",
                });
                return;
            }
            // When dedup is required, pass messageId + window to the store so repeats are dropped.
            var messageId = _descriptor.RequiresDuplicateDetection ? msg.Properties?.MessageId?.ToString() : null;
            var dedupWindow = _descriptor.RequiresDuplicateDetection
                ? _descriptor.DuplicateDetectionHistoryTimeWindow ?? TimeSpan.FromMinutes(10)
                : (TimeSpan?)null;
            // Forward target may be a topic - build a filter context unconditionally so
            // the router can fan-out if the chain hits one. Cheap when not forwarded.
            var filterContext = BuildFilterContext(msg, _timeProvider.GetUtcNow());

            // If the client wrapped this transfer in a TransactionalState, buffer the
            // enqueue under the txn and reply with a transactional Accepted instead of running
            // the actual store op now. Commit/rollback happens via the coordinator link.
            if (messageContext.DeliveryState is TransactionalState txnState && txnState.TxnId is { Length: > 0 } txnId)
            {
                if (_transactions.Enlist(txnId, ct => RouteOrEnqueueAsync(encoded, expiresAt, scheduledFor, sessionId, messageId, dedupWindow, filterContext)))
                {
                    messageContext.Link.DisposeMessage(messageContext.Message,
                        new TransactionalState { TxnId = txnId, Outcome = new Accepted() },
                        settled: true);
                }
                else
                {
                    messageContext.Complete(new Error(new Symbol(ErrorCode.IllegalState))
                    {
                        Description = "Unknown or already-discharged transaction id.",
                    });
                }
                return;
            }

            _ = EnqueueAndCompleteAsync(messageContext, encoded, expiresAt, scheduledFor, sessionId, messageId, dedupWindow, filterContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encode incoming message on queue {Queue}", _queueName);
            messageContext.Complete(new Error(new Symbol(ErrorCode.InternalError))
            {
                Description = "Failed to accept message",
            });
        }
    }

    private async Task EnqueueBatchAsync(MessageContext context, DataList dataList, byte[]? txnId)
    {
        try
        {
            var enlisted = true;
            for (var i = 0; i < dataList.Count; i++)
            {
                var innerBinary = dataList[i].Binary;
                // Copy: AMQPNetLite may pool the underlying buffer; we need an independent byte[].
                var innerBytes = new byte[innerBinary.Length];
                Array.Copy(innerBinary, innerBytes, innerBinary.Length);

                // Each inner message has its own header (TTL) and annotations (scheduled-enqueue-time).
                var inner = DecodeMessage(innerBytes);
                var expiresAt = ComputeExpiresAt(inner);
                var scheduledFor = ReadScheduledEnqueueTime(inner);
                var sessionId = _descriptor.RequiresSession ? inner.Properties?.GroupId : null;
                if (_descriptor.RequiresSession && string.IsNullOrEmpty(sessionId))
                {
                    // Same session guard as the single-message path. Messages before this one
                    // in the batch are already enqueued; Azure validates client-side so mixed
                    // batches don't normally reach a broker at all.
                    context.Complete(new Error(new Symbol(ErrorCode.NotAllowed))
                    {
                        Description = $"A SessionId is required for messages sent to session-enabled entity '{_queueName}'.",
                    });
                    return;
                }
                var messageId = _descriptor.RequiresDuplicateDetection ? inner.Properties?.MessageId?.ToString() : null;
                var dedupWindow = _descriptor.RequiresDuplicateDetection
                    ? _descriptor.DuplicateDetectionHistoryTimeWindow ?? TimeSpan.FromMinutes(10)
                    : (TimeSpan?)null;
                var filterContext = BuildFilterContext(inner, _timeProvider.GetUtcNow());

                if (txnId is { Length: > 0 })
                {
                    if (!_transactions.Enlist(txnId, _ => RouteOrEnqueueAsync(innerBytes, expiresAt, scheduledFor, sessionId, messageId, dedupWindow, filterContext)))
                    {
                        enlisted = false;
                        break;
                    }
                }
                else
                {
                    await RouteOrEnqueueAsync(innerBytes, expiresAt, scheduledFor, sessionId, messageId, dedupWindow, filterContext).ConfigureAwait(false);
                }
            }

            if (txnId is { Length: > 0 })
            {
                if (enlisted)
                {
                    context.Link.DisposeMessage(context.Message,
                        new TransactionalState { TxnId = txnId, Outcome = new Accepted() }, settled: true);
                }
                else
                {
                    context.Complete(new Error(new Symbol(ErrorCode.IllegalState)) { Description = "Unknown or already-discharged transaction id." });
                }
            }
            else
            {
                _logger.LogDebug("Split batched envelope into {Count} message(s) on {Queue}", dataList.Count, _queueName);
                context.Complete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue batched messages on queue {Queue}", _queueName);
            context.Complete(new Error(new Symbol(ErrorCode.InternalError)) { Description = ex.Message });
        }
    }

    private static Message DecodeMessage(byte[] bytes)
    {
        var buf = new ByteBuffer(bytes, 0, bytes.Length, bytes.Length);
        return Message.Decode(buf);
    }

    private async Task EnqueueAndCompleteAsync(MessageContext context, byte[] encoded, DateTimeOffset? expiresAt, DateTimeOffset? scheduledFor, string? sessionId, string? messageId, TimeSpan? duplicateDetectionWindow, MessageFilterContext filterContext)
    {
        try
        {
            await RouteOrEnqueueAsync(encoded, expiresAt, scheduledFor, sessionId, messageId, duplicateDetectionWindow, filterContext).ConfigureAwait(false);
            _logger.LogDebug("Accepted send on {Queue} ({Bytes} bytes, expiresAt={Expires}, scheduledFor={Scheduled})",
                _queueName, encoded.Length,
                expiresAt?.ToString("O") ?? "never",
                scheduledFor?.ToString("O") ?? "immediate");
            context.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue message on queue {Queue}", _queueName);
            context.Complete(new Error(new Symbol(ErrorCode.InternalError))
            {
                Description = ex.Message,
            });
        }
    }

    /// <summary>
    /// Hook for auto-forwarding + the send-instrumentation point. Every accepted send
    /// (single, batched, or transactional commit-replay) flows through here exactly once per
    /// message - making it the single right place to emit the <c>osb.send</c> activity and bump
    /// the sent counter.
    /// </summary>
    private async Task RouteOrEnqueueAsync(byte[] encoded, DateTimeOffset? expiresAt, DateTimeOffset? scheduledFor, string? sessionId, string? messageId, TimeSpan? dedupWindow, MessageFilterContext filterContext)
    {
        using var activity = OpenServiceBusDiagnostics.ActivitySource.StartActivity(
            OpenServiceBusDiagnostics.SpanSend, ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag(OpenServiceBusDiagnostics.TagSystem, OpenServiceBusDiagnostics.SystemValue);
            activity.SetTag(OpenServiceBusDiagnostics.TagDestination, _queueName);
            activity.SetTag(OpenServiceBusDiagnostics.TagOperation, "send");
            if (filterContext.MessageId is { } mid)
                activity.SetTag(OpenServiceBusDiagnostics.TagMessageId, mid);
            if (filterContext.CorrelationId is { } cid)
                activity.SetTag(OpenServiceBusDiagnostics.TagConversationId, cid);
            if (sessionId is not null)
                activity.SetTag(OpenServiceBusDiagnostics.TagSessionId, sessionId);
        }

        try
        {
            if (!string.IsNullOrEmpty(_descriptor.ForwardTo))
            {
                await _router.RouteAsync(
                    _descriptor.ForwardTo, encoded, expiresAt, scheduledFor,
                    sessionId, messageId, dedupWindow, filterContext).ConfigureAwait(false);
            }
            else
            {
                await _store.EnqueueAsync(_queueName, encoded, expiresAt, scheduledFor, sessionId, messageId, dedupWindow).ConfigureAwait(false);
            }
            OpenServiceBusDiagnostics.MessagesSent.Add(1,
                new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDestination, _queueName));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static MessageFilterContext BuildFilterContext(Message msg, DateTimeOffset enqueuedAt)
    {
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (msg.ApplicationProperties is not null)
        {
            foreach (var key in msg.ApplicationProperties.Map.Keys)
            {
                if (key is null) continue;
                appProps[key.ToString()!] = msg.ApplicationProperties.Map[key];
            }
        }
        return new MessageFilterContext
        {
            MessageId = msg.Properties?.MessageId,
            CorrelationId = msg.Properties?.CorrelationId,
            Subject = msg.Properties?.Subject,
            To = msg.Properties?.To,
            ReplyTo = msg.Properties?.ReplyTo,
            ReplyToSessionId = msg.Properties?.ReplyToGroupId,
            SessionId = msg.Properties?.GroupId,
            ContentType = msg.Properties?.ContentType,
            EnqueuedTimeUtc = enqueuedAt,
            ApplicationProperties = appProps,
        };
    }

    private static DateTimeOffset? ReadScheduledEnqueueTime(Message msg)
    {
        if (msg.MessageAnnotations is null) return null;
        if (!msg.MessageAnnotations.Map.TryGetValue(ScheduledEnqueueTimeSymbol, out var value)) return null;
        return value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind).ToUniversalTime()),
            DateTimeOffset dto => dto,
            _ => null,
        };
    }

    /// <summary>
    /// Compute the effective TTL deadline: <c>now + min(perMessageTtl, queueDefaultTtl)</c>.
    /// Returns null when neither side specifies a TTL.
    /// </summary>
    private DateTimeOffset? ComputeExpiresAt(Message msg)
    {
        TimeSpan? perMessage = msg.Header?.Ttl is uint ms and > 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;
        var queueDefault = _descriptor.DefaultMessageTimeToLive;

        var effective = MinTtl(perMessage, queueDefault);
        return effective is null ? null : _timeProvider.GetUtcNow() + effective.Value;
    }

    private static TimeSpan? MinTtl(TimeSpan? a, TimeSpan? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a < b ? a : b;
    }

    private static long PayloadSize(Message message)
    {
        return message.Encode().Length;
    }

    private static byte[] CopyEncoded(Message message)
    {
        var buffer = message.Encode();
        var copy = new byte[buffer.Length];
        Array.Copy(buffer.Buffer, buffer.Offset, copy, 0, buffer.Length);
        return copy;
    }
}
