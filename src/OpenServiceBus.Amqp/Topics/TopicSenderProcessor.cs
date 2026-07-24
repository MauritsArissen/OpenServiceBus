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
            var msg = messageContext.Message;

            if (msg.Format == AmqpBatchedMessageFormat && msg.BodySection is DataList dataList)
            {
                var batchTxnId = (messageContext.DeliveryState as TransactionalState)?.TxnId;
                _ = FanOutBatchAsync(messageContext, dataList, batchTxnId);
                return;
            }

            var encoded = CopyEncoded(msg);
            var expiresAt = ComputeExpiresAt(msg);
            var scheduledFor = ReadScheduledEnqueueTime(msg);
            var filterContext = BuildFilterContext(msg, _timeProvider.GetUtcNow());

            // Transactional fan-out - buffer the route-and-fanout under the txn so it
            // only happens on commit. Each enlist captures the same byte[] + filter context.
            if (messageContext.DeliveryState is TransactionalState txnState && txnState.TxnId is { Length: > 0 } txnId)
            {
                if (_transactions.Enlist(txnId, _ => _router.RouteAsync(_topic.Name, encoded, expiresAt, scheduledFor, sessionId: null, filterContext: filterContext)))
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
            _ = FanOutAndCompleteAsync(messageContext, encoded, expiresAt, scheduledFor, filterContext, sessionId: null);
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

    private async Task FanOutAndCompleteAsync(
        MessageContext context,
        byte[] encoded,
        DateTimeOffset? expiresAt,
        DateTimeOffset? scheduledFor,
        MessageFilterContext filterContext,
        string? sessionId)
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

            // Routing the topic name itself triggers the router's fan-out path, which also
            // walks each subscription's ForwardTo before landing on a backing queue.
            var landed = await _router.RouteAsync(_topic.Name, encoded, expiresAt, scheduledFor, sessionId, filterContext: filterContext).ConfigureAwait(false);
            activity?.SetTag("osb.fanout.subscribers", landed.Count);
            OpenServiceBusDiagnostics.MessagesSent.Add(1,
                new KeyValuePair<string, object?>(OpenServiceBusDiagnostics.TagDestination, _topic.Name));
            _logger.LogDebug("Fanned out 1 message on topic {Topic} to {Count} subscriber(s)", _topic.Name, landed.Count);
            context.Complete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fan-out message on topic {Topic}", _topic.Name);
            context.Complete(new Error(new Symbol(ErrorCode.InternalError)) { Description = ex.Message });
        }
    }

    private async Task FanOutBatchAsync(MessageContext context, DataList dataList, byte[]? txnId)
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

                if (txnId is { Length: > 0 })
                {
                    // Buffer the fan-out under the txn; it only runs on commit.
                    if (!_transactions.Enlist(txnId, _ => _router.RouteAsync(_topic.Name, innerBytes, expiresAt, scheduledFor, sessionId: null, filterContext: filterContext)))
                    {
                        enlisted = false;
                        break;
                    }
                }
                else
                {
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fan-out batched envelope on topic {Topic}", _topic.Name);
            context.Complete(new Error(new Symbol(ErrorCode.InternalError)) { Description = ex.Message });
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
