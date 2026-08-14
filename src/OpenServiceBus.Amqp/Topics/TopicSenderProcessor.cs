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
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Core.Transactions;

namespace OpenServiceBus.Amqp.Topics;

/// <summary>
/// Handles incoming AMQP sender links targeted at a <see cref="TopicDescriptor"/>. Mirrors
/// <see cref="Queues.QueueSenderProcessor"/> but, instead of enqueuing to a single queue,
/// asks the topic registry which subscription backing queues should receive a copy and
/// enqueues to each of them.
/// </summary>
public sealed class TopicSenderProcessor : IMessageProcessor
{
    private static readonly Symbol ScheduledEnqueueTimeSymbol = new("x-opt-scheduled-enqueue-time");
    private const uint AmqpBatchedMessageFormat = 0x80013700u;

    private readonly TopicDescriptor _topic;
    private readonly ITopicRegistry _topics;
    private readonly IMessageStore _store;
    private readonly IMessageRouter _router;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TopicSenderProcessor> _logger;

    public TopicSenderProcessor(
        TopicDescriptor topic,
        ITopicRegistry topics,
        IMessageStore store,
        IMessageRouter router,
        ITransactionManager transactions,
        TimeProvider timeProvider,
        ILogger<TopicSenderProcessor> logger)
    {
        _topic = topic;
        _topics = topics;
        _store = store;
        _router = router;
        _transactions = transactions;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public int Credit => 100;

    public void Process(MessageContext messageContext)
    {
        try
        {
            var resolvedTopic = _topics.GetTopicAsync(_topic.Name).GetAwaiter().GetResult();
            if (resolvedTopic is null)
            {
                CompleteEntityDeleted(messageContext);
                return;
            }
            var currentStatus = resolvedTopic.Status;
            if (!currentStatus.AcceptsSends())
            {
                messageContext.Complete(new Error(new Symbol(Routing.ServiceBusErrors.EntityDisabled))
                {
                    Info = new Fields(),
                    Description = $"Topic '{_topic.Name}' is {currentStatus} and does not accept messages.",
                });
                return;
            }

            var msg = messageContext.Message;

            var currentTopic = resolvedTopic;
            var payloadBytes = (long)msg.Encode().Length;
            if (payloadBytes > currentTopic.MaxMessageSizeInKilobytes * 1024)
            {
                messageContext.Complete(new Error(new Symbol(Routing.ServiceBusErrors.MessageSizeExceeded))
                {
                    Info = new Fields(),
                    Description = $"Message of {payloadBytes} bytes exceeds the limit of {currentTopic.MaxMessageSizeInKilobytes} KB on '{_topic.Name}'.",
                });
                return;
            }
            var usage = TopicUsageBytes(currentTopic.Name);
            if (usage + payloadBytes > currentTopic.MaxSizeInMegabytes * 1024 * 1024)
            {
                messageContext.Complete(new Error(new Symbol(Routing.ServiceBusErrors.QuotaExceeded))
                {
                    Info = new Fields(),
                    Description = $"Topic '{_topic.Name}' has reached its {currentTopic.MaxSizeInMegabytes} MB quota.",
                });
                return;
            }

            if (msg.Format == AmqpBatchedMessageFormat && msg.BodySection is DataList dataList)
            {
                var batchTxnId = (messageContext.DeliveryState as TransactionalState)?.TxnId;
                var batchDedupWindow = DuplicateDetection.EffectiveWindow(
                    currentTopic.RequiresDuplicateDetection, currentTopic.DuplicateDetectionHistoryTimeWindow);
                _ = FanOutBatchAsync(messageContext, dataList, batchTxnId, batchDedupWindow);
                return;
            }

            var encoded = CopyEncoded(msg);
            var expiresAt = ComputeExpiresAt(msg);
            var scheduledFor = ReadScheduledEnqueueTime(msg);
            var filterContext = BuildFilterContext(msg, _timeProvider.GetUtcNow());

            // Topic-level duplicate detection runs ONCE per publish, before fan-out, so a
            // duplicate reaches zero subscriptions - matching Azure. Scheduled publishes are
            // checked at send time (now), not at activation, also matching Azure. The check
            // uses the freshly resolved descriptor so config changes apply without re-attach.
            var dedupWindow = DuplicateDetection.EffectiveWindow(
                currentTopic.RequiresDuplicateDetection, currentTopic.DuplicateDetectionHistoryTimeWindow);
            var messageId = dedupWindow is not null ? msg.Properties?.MessageId?.ToString() : null;

            // Transactional fan-out - buffer the route-and-fanout under the txn so it
            // only happens on commit. Each enlist captures the same byte[] + filter context;
            // the dedup check also runs at commit time, matching the queue path.
            if (messageContext.DeliveryState is TransactionalState txnState && txnState.TxnId is { Length: > 0 } txnId)
            {
                if (_transactions.Enlist(txnId, async _ =>
                    {
                        if (await IsDuplicateAsync(messageId, dedupWindow).ConfigureAwait(false)) return;
                        await _router.RouteAsync(_topic.Name, encoded, expiresAt, scheduledFor, sessionId: null, filterContext: filterContext).ConfigureAwait(false);
                    }))
                {
                    messageContext.Link.DisposeMessage(messageContext.Message,
                        new TransactionalState { TxnId = txnId, Outcome = new Accepted() }, settled: true);
                }
                else
                {
                    messageContext.Complete(new Error(new Symbol(ErrorCode.IllegalState)) { Description = "Unknown or already-discharged transaction id." });
                }
                return;
            }

            // Note: session routing isn't yet threaded through topic fan-out; subscriptions
            // with RequiresSession are accepted at creation time but messages pass via the
            // regular channel. Lifted when EvaluateSubscribers returns descriptors.
            _ = FanOutAndCompleteAsync(messageContext, encoded, expiresAt, scheduledFor, filterContext, sessionId: null, messageId, dedupWindow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept message on topic {Topic}", _topic.Name);
            messageContext.Complete(new Error(new Symbol(ErrorCode.InternalError))
            {
                Description = "Failed to accept message",
            });
        }
    }

    private async Task<bool> IsDuplicateAsync(string? messageId, TimeSpan? dedupWindow)
    {
        if (dedupWindow is not { } window || string.IsNullOrEmpty(messageId)) return false;
        var duplicate = await _store.CheckTopicDuplicateAsync(_topic.Name, messageId, window).ConfigureAwait(false);
        if (duplicate)
        {
            _logger.LogDebug("Dropped duplicate MessageId '{MessageId}' on topic {Topic} before fan-out (window {Window})",
                messageId, _topic.Name, window);
        }
        return duplicate;
    }

    private async Task FanOutAndCompleteAsync(
        MessageContext context,
        byte[] encoded,
        DateTimeOffset? expiresAt,
        DateTimeOffset? scheduledFor,
        MessageFilterContext filterContext,
        string? sessionId,
        string? messageId = null,
        TimeSpan? dedupWindow = null)
    {
        try
        {
            using var activity = OpenServiceBusDiagnostics.ActivitySource.StartActivity(
                OpenServiceBusDiagnostics.SpanSend, ActivityKind.Producer);
            if (activity is not null)
            {
                activity.SetTag(OpenServiceBusDiagnostics.TagSystem, OpenServiceBusDiagnostics.SystemValue);
                activity.SetTag(OpenServiceBusDiagnostics.TagDestination, _topic.Name);
                activity.SetTag(OpenServiceBusDiagnostics.TagOperation, "publish");
                if (filterContext.MessageId is { } mid) activity.SetTag(OpenServiceBusDiagnostics.TagMessageId, mid);
                if (filterContext.CorrelationId is { } cid) activity.SetTag(OpenServiceBusDiagnostics.TagConversationId, cid);
            }

            // Silent drop on a dedup hit: the sender still sees Accepted, no subscription
            // sees a copy - identical to how the queue path treats duplicates.
            if (await IsDuplicateAsync(messageId, dedupWindow).ConfigureAwait(false))
            {
                activity?.SetTag("osb.dedup.dropped", true);
                activity?.SetTag("osb.fanout.subscribers", 0);
                context.Complete();
                return;
            }

            // Routing the topic name itself triggers the router's fan-out path, which also
            // walks each subscription's ForwardTo before landing on a backing queue.
            var landed = await _router.RouteAsync(_topic.Name, encoded, expiresAt, scheduledFor, sessionId, filterContext: filterContext).ConfigureAwait(false);
            activity?.SetTag("osb.fanout.subscribers", landed.Count);
            OpenServiceBusDiagnostics.MessagesSent.Add(1,
                new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDestination, _topic.Name));
            _logger.LogDebug("Fanned out 1 message on topic {Topic} to {Count} subscriber(s)", _topic.Name, landed.Count);
            context.Complete();
        }
        catch (InvalidOperationException) when (TopicDeleted())
        {
            CompleteEntityDeleted(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fan-out message on topic {Topic}", _topic.Name);
            context.Complete(new Error(new Symbol(ErrorCode.InternalError)) { Description = ex.Message });
        }
    }

    private bool TopicDeleted() =>
        _topics.GetTopicAsync(_topic.Name).GetAwaiter().GetResult() is null;

    private void CompleteEntityDeleted(MessageContext context)
    {
        context.Complete(new Error(new Symbol(ErrorCode.NotFound))
        {
            Info = new Fields(),
            Description = $"The messaging entity '{_topic.Name}' has been deleted.",
        });
    }

    private async Task FanOutBatchAsync(MessageContext context, DataList dataList, byte[]? txnId, TimeSpan? dedupWindow)
    {
        try
        {
            var enlisted = true;
            for (var i = 0; i < dataList.Count; i++)
            {
                var innerBinary = dataList[i].Binary;
                var innerBytes = new byte[innerBinary.Length];
                Array.Copy(innerBinary, innerBytes, innerBinary.Length);

                var inner = DecodeMessage(innerBytes);
                var expiresAt = ComputeExpiresAt(inner);
                var scheduledFor = ReadScheduledEnqueueTime(inner);
                var filterContext = BuildFilterContext(inner, _timeProvider.GetUtcNow());
                // Each inner message of a batched envelope is checked individually,
                // consistent with the queue path.
                var messageId = dedupWindow is not null ? inner.Properties?.MessageId?.ToString() : null;

                if (txnId is { Length: > 0 })
                {
                    // Buffer the fan-out under the txn; it only runs on commit.
                    if (!_transactions.Enlist(txnId, async _ =>
                        {
                            if (await IsDuplicateAsync(messageId, dedupWindow).ConfigureAwait(false)) return;
                            await _router.RouteAsync(_topic.Name, innerBytes, expiresAt, scheduledFor, sessionId: null, filterContext: filterContext).ConfigureAwait(false);
                        }))
                    {
                        enlisted = false;
                        break;
                    }
                }
                else
                {
                    if (await IsDuplicateAsync(messageId, dedupWindow).ConfigureAwait(false)) continue;
                    await _router.RouteAsync(_topic.Name, innerBytes, expiresAt, scheduledFor, sessionId: null, filterContext: filterContext).ConfigureAwait(false);
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
                context.Complete();
            }
        }
        catch (InvalidOperationException) when (TopicDeleted())
        {
            CompleteEntityDeleted(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fan-out batched envelope on topic {Topic}", _topic.Name);
            context.Complete(new Error(new Symbol(ErrorCode.InternalError)) { Description = ex.Message });
        }
    }

    private long TopicUsageBytes(string topicName)
    {
        long usage = 0;
        foreach (var sub in _topics.ListSubscriptionsAsync(topicName).GetAwaiter().GetResult())
        {
            usage += _store.GetSizeInBytes(sub.BackingQueueName);
            usage += _store.GetSizeInBytes(sub.BackingQueueName + EntityNames.DeadLetterSuffix);
            usage += _store.GetSizeInBytes(sub.BackingQueueName + EntityNames.TransferDeadLetterSuffix);
        }
        return usage;
    }

    private static MessageFilterContext BuildFilterContext(Message msg, DateTimeOffset enqueuedAt) =>
        AmqpFilterContext.FromMessage(msg, enqueuedAt);

    private static Message DecodeMessage(byte[] bytes)
    {
        var buf = new ByteBuffer(bytes, 0, bytes.Length, bytes.Length);
        return Message.Decode(buf);
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

    private DateTimeOffset? ComputeExpiresAt(Message msg)
    {
        TimeSpan? perMessage = msg.Header?.Ttl is uint ms and > 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;
        var topicDefault = _topic.DefaultMessageTimeToLive;
        var effective = perMessage is null ? topicDefault
                      : topicDefault is null ? perMessage
                      : perMessage < topicDefault ? perMessage : topicDefault;
        return effective is null ? null : _timeProvider.GetUtcNow() + effective.Value;
    }

    private static byte[] CopyEncoded(Message message)
    {
        var buffer = message.Encode();
        var copy = new byte[buffer.Length];
        Array.Copy(buffer.Buffer, buffer.Offset, copy, 0, buffer.Length);
        return copy;
    }
}
