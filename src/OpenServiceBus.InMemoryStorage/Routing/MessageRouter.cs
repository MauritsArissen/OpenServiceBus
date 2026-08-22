using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.Routing;

/// <summary>
/// Default <see cref="IMessageRouter"/> implementation. Resolves the target against the
/// queue and topic registries on each hop, follows <c>ForwardTo</c> chains until the cap is
/// hit, and fans out at topics by delegating to <see cref="ITopicRegistry.EvaluateSubscribers"/>.
/// </summary>
public sealed class MessageRouter : IMessageRouter
{
    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry? _topics;
    private readonly IMessageStore _store;
    private readonly ILogger<MessageRouter> _logger;
    private readonly IRuleActionApplier? _actionApplier;
    private readonly EntityActivityTracker? _activity;
    private readonly IDeadLetterAnnotator? _deadLetterAnnotator;

    public MessageRouter(
        IQueueRegistry queues,
        IMessageStore store,
        ILogger<MessageRouter> logger,
        ITopicRegistry? topics = null,
        IRuleActionApplier? actionApplier = null,
        EntityActivityTracker? activityTracker = null,
        IDeadLetterAnnotator? deadLetterAnnotator = null)
    {
        _queues = queues;
        _topics = topics;
        _store = store;
        _logger = logger;
        _actionApplier = actionApplier;
        _activity = activityTracker;
        _deadLetterAnnotator = deadLetterAnnotator;
    }

    public async Task<IReadOnlyList<string>> RouteAsync(
        string targetEntityName,
        byte[] encodedMessage,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? scheduledEnqueueTime = null,
        string? sessionId = null,
        string? messageId = null,
        TimeSpan? duplicateDetectionWindow = null,
        MessageFilterContext? filterContext = null,
        int deliveryCount = 0,
        string? forwardSource = null,
        long? enqueuedSequenceNumber = null,
        CancellationToken cancellationToken = default)
    {
        var landed = new List<string>();
        await RouteInternalAsync(
            targetEntityName, encodedMessage, expiresAt, scheduledEnqueueTime,
            sessionId, messageId, duplicateDetectionWindow, filterContext, deliveryCount,
            depth: 0, landed, forwardSource, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
        return landed;
    }

    private async Task RouteInternalAsync(
        string targetEntityName,
        byte[] encoded,
        DateTimeOffset? expiresAt,
        DateTimeOffset? scheduledFor,
        string? sessionId,
        string? messageId,
        TimeSpan? dedupWindow,
        MessageFilterContext? filterContext,
        int deliveryCount,
        int depth,
        List<string> landed,
        string? forwardSource,
        long? enqueuedSequenceNumber,
        CancellationToken cancellationToken)
    {
        var maxDepth = ((IMessageRouter)this).MaxForwardDepth;
        if (depth >= maxDepth)
        {
            if (forwardSource is not null)
            {
                await TransferDeadLetterAsync(
                    forwardSource, encoded, "MaxTransferHopCountExceeded",
                    $"The maximum number of transfer hops ({maxDepth}) was exceeded while forwarding to '{targetEntityName}'.",
                    deliveryCount, landed, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                return;
            }
            _logger.LogWarning(
                "Auto-forward chain exceeded {MaxDepth} hops at '{Target}' - message dropped to prevent loops.",
                maxDepth, targetEntityName);
            return;
        }

        // 1. Topic fan-out: if the name resolves to a topic, evaluate rules and recurse for
        //    each matching subscription. Subscriptions themselves may have ForwardTo set.
        if (_topics is not null)
        {
            var topic = await _topics.GetTopicAsync(targetEntityName, cancellationToken).ConfigureAwait(false);
            if (topic is not null)
            {
                if (forwardSource is not null)
                {
                    if (!topic.Status.AcceptsSends())
                    {
                        await TransferDeadLetterAsync(
                            forwardSource, encoded, "MessagingEntityDisabled",
                            $"The forward target topic '{topic.Name}' is {topic.Status} and does not accept messages.",
                            deliveryCount, landed, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    var topicUsage = await TopicUsageBytesAsync(topic.Name, cancellationToken).ConfigureAwait(false);
                    if (topicUsage + encoded.Length > topic.MaxSizeInMegabytes * 1024L * 1024L)
                    {
                        await TransferDeadLetterAsync(
                            forwardSource, encoded, "QuotaExceeded",
                            $"The forward target topic '{topic.Name}' has reached its {topic.MaxSizeInMegabytes} MB quota.",
                            deliveryCount, landed, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                if (filterContext is null)
                {
                    _logger.LogWarning(
                        "Cannot fan-out at '{Topic}' without a filter context - message dropped. " +
                        "This usually means a queue's ForwardTo points at a topic; pass a filter context through the call site.",
                        topic.Name);
                    return;
                }

                // The publish-side sequence number lives on the topic: every fan-out copy
                // carries the same x-opt-enqueue-sequence-number, allocated once per publish.
                enqueuedSequenceNumber ??= await _store.AllocateSequenceNumberAsync(topic.Name, cancellationToken).ConfigureAwait(false);

                var matched = _topics.EvaluateSubscriberMatches(topic.Name, filterContext);

                foreach (var (sub, action) in matched)
                {
                    // SendDisabled stops NEW copies entering the subscription; Disabled keeps
                    // accepting copies (frozen for receive) so a drain-and-re-enable loses
                    // nothing. See docs/Entity-Status.md.
                    if (sub.Status == EntityStatus.SendDisabled)
                    {
                        continue;
                    }

                    var subEncoded = encoded;
                    if (action is not null && _actionApplier is not null)
                    {
                        try
                        {
                            subEncoded = _actionApplier.Apply(encoded, action);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "SQL rule action failed on '{Subscription}' - delivering the copy unmodified. Action: {Action}",
                                sub.BackingQueueName, action.Expression);
                        }
                    }

                    if (!string.IsNullOrEmpty(sub.ForwardTo))
                    {
                        await RouteInternalAsync(sub.ForwardTo, subEncoded, expiresAt, scheduledFor,
                            sessionId, messageId, dedupWindow, filterContext, deliveryCount,
                            depth + 1, landed, sub.BackingQueueName, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var subSessionId = sub.RequiresSession ? (sessionId ?? filterContext?.SessionId) : null;
                    if (sub.RequiresSession && string.IsNullOrEmpty(subSessionId))
                    {
                        var dlq = sub.BackingQueueName + "/$DeadLetterQueue";
                        await _store.EnqueueAsync(
                            dlq, subEncoded, expiresAt, scheduledFor,
                            sessionId: null, messageId, dedupWindow, deliveryCount, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                        landed.Add(dlq);
                        _logger.LogWarning(
                            "Message without a session id matched session-enabled subscription '{Subscription}' - copy dead-lettered.",
                            sub.BackingQueueName);
                        continue;
                    }

                    await _store.EnqueueAsync(
                        sub.BackingQueueName, subEncoded, expiresAt, scheduledFor,
                        subSessionId, messageId, dedupWindow, deliveryCount, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                    landed.Add(sub.BackingQueueName);
                    _activity?.Touch(sub.BackingQueueName);
                }
                return;
            }
        }

        // 2. Queue path: if the queue has ForwardTo, chain. Otherwise enqueue here.
        var queue = await _queues.GetAsync(targetEntityName, cancellationToken).ConfigureAwait(false);
        if (queue is null)
        {
            if (forwardSource is not null)
            {
                await TransferDeadLetterAsync(
                    forwardSource, encoded, "MessagingEntityNotFound",
                    $"The forward target '{targetEntityName}' does not exist.",
                    deliveryCount, landed, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                return;
            }
            _logger.LogWarning("Routing target '{Target}' resolves to neither a topic nor a queue - message dropped.", targetEntityName);
            return;
        }

        if (forwardSource is not null && !queue.Status.AcceptsSends())
        {
            await TransferDeadLetterAsync(
                forwardSource, encoded, "MessagingEntityDisabled",
                $"The forward target '{queue.Name}' is {queue.Status} and does not accept messages.",
                deliveryCount, landed, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(queue.ForwardTo))
        {
            // A forwarding queue is a transparent passthrough that never stores the message,
            // but it IS the entity the client sent to - mint its sequence number here so the
            // final copy's x-opt-enqueue-sequence-number reflects the original entity.
            enqueuedSequenceNumber ??= await _store.AllocateSequenceNumberAsync(queue.Name, cancellationToken).ConfigureAwait(false);
            await RouteInternalAsync(queue.ForwardTo, encoded, expiresAt, scheduledFor,
                sessionId, messageId, dedupWindow, filterContext, deliveryCount,
                depth + 1, landed, queue.Name, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (forwardSource is not null)
        {
            var usage = _store.GetSizeInBytes(queue.Name)
                + _store.GetSizeInBytes(queue.Name + EntityNames.DeadLetterSuffix)
                + _store.GetSizeInBytes(queue.Name + EntityNames.TransferDeadLetterSuffix);
            if (usage + encoded.Length > queue.MaxSizeInMegabytes * 1024L * 1024L)
            {
                await TransferDeadLetterAsync(
                    forwardSource, encoded, "QuotaExceeded",
                    $"The forward target '{queue.Name}' has reached its {queue.MaxSizeInMegabytes} MB quota.",
                    deliveryCount, landed, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await _store.EnqueueAsync(
            queue.Name, encoded, expiresAt, scheduledFor,
            sessionId, messageId, dedupWindow, deliveryCount, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
        landed.Add(queue.Name);
        _activity?.Touch(queue.Name);
    }

    /// <summary>
    /// A forward hop could not be delivered: move the message to the forwarding entity's
    /// transfer dead-letter queue with the Service Bus dead-letter metadata stamped on.
    /// Mirrors regular dead-lettering: no session affinity, no scheduling, no TTL.
    /// </summary>
    private async Task TransferDeadLetterAsync(
        string sourceEntity,
        byte[] encoded,
        string reason,
        string description,
        int deliveryCount,
        List<string> landed,
        long? enqueuedSequenceNumber,
        CancellationToken cancellationToken)
    {
        var target = sourceEntity + EntityNames.TransferDeadLetterSuffix;
        var stamped = _deadLetterAnnotator?.Annotate(encoded, sourceEntity, reason, description) ?? encoded;
        try
        {
            await _store.EnqueueAsync(
                target, stamped, expiresAt: null, scheduledEnqueueTime: null,
                sessionId: null, messageId: null, duplicateDetectionWindow: null,
                deliveryCount, enqueuedSequenceNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            _logger.LogWarning(
                "Transfer dead-letter queue '{Target}' does not exist - message from '{Source}' dropped ({Reason}).",
                target, sourceEntity, reason);
            return;
        }
        landed.Add(target);
        _activity?.Touch(sourceEntity);
        _logger.LogWarning(
            "Forward hop from '{Source}' failed ({Reason}) - message moved to its transfer dead-letter queue. {Description}",
            sourceEntity, reason, description);
    }

    private async Task<long> TopicUsageBytesAsync(string topicName, CancellationToken cancellationToken)
    {
        long usage = 0;
        foreach (var sub in await _topics!.ListSubscriptionsAsync(topicName, cancellationToken).ConfigureAwait(false))
        {
            usage += _store.GetSizeInBytes(sub.BackingQueueName);
            usage += _store.GetSizeInBytes(sub.BackingQueueName + EntityNames.DeadLetterSuffix);
            usage += _store.GetSizeInBytes(sub.BackingQueueName + EntityNames.TransferDeadLetterSuffix);
        }
        return usage;
    }
}
