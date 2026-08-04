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

    public MessageRouter(
        IQueueRegistry queues,
        IMessageStore store,
        ILogger<MessageRouter> logger,
        ITopicRegistry? topics = null,
        IRuleActionApplier? actionApplier = null,
        EntityActivityTracker? activityTracker = null)
    {
        _queues = queues;
        _topics = topics;
        _store = store;
        _logger = logger;
        _actionApplier = actionApplier;
        _activity = activityTracker;
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
        CancellationToken cancellationToken = default)
    {
        var landed = new List<string>();
        await RouteInternalAsync(
            targetEntityName, encodedMessage, expiresAt, scheduledEnqueueTime,
            sessionId, messageId, duplicateDetectionWindow, filterContext, deliveryCount,
            depth: 0, landed, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        if (depth >= ((IMessageRouter)this).MaxForwardDepth)
        {
            _logger.LogWarning(
                "Auto-forward chain exceeded {MaxDepth} hops at '{Target}' - message dropped to prevent loops.",
                ((IMessageRouter)this).MaxForwardDepth, targetEntityName);
            return;
        }

        // 1. Topic fan-out: if the name resolves to a topic, evaluate rules and recurse for
        //    each matching subscription. Subscriptions themselves may have ForwardTo set.
        if (_topics is not null)
        {
            var topic = await _topics.GetTopicAsync(targetEntityName, cancellationToken).ConfigureAwait(false);
            if (topic is not null)
            {
                if (filterContext is null)
                {
                    _logger.LogWarning(
                        "Cannot fan-out at '{Topic}' without a filter context - message dropped. " +
                        "This usually means a queue's ForwardTo points at a topic; pass a filter context through the call site.",
                        topic.Name);
                    return;
                }

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
                            depth + 1, landed, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var subSessionId = sub.RequiresSession ? (sessionId ?? filterContext?.SessionId) : null;
                    if (sub.RequiresSession && string.IsNullOrEmpty(subSessionId))
                    {
                        var dlq = sub.BackingQueueName + "/$DeadLetterQueue";
                        await _store.EnqueueAsync(
                            dlq, subEncoded, expiresAt, scheduledFor,
                            sessionId: null, messageId, dedupWindow, deliveryCount, cancellationToken).ConfigureAwait(false);
                        landed.Add(dlq);
                        _logger.LogWarning(
                            "Message without a session id matched session-enabled subscription '{Subscription}' - copy dead-lettered.",
                            sub.BackingQueueName);
                        continue;
                    }

                    await _store.EnqueueAsync(
                        sub.BackingQueueName, subEncoded, expiresAt, scheduledFor,
                        subSessionId, messageId, dedupWindow, deliveryCount, cancellationToken).ConfigureAwait(false);
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
            _logger.LogWarning("Routing target '{Target}' resolves to neither a topic nor a queue - message dropped.", targetEntityName);
            return;
        }

        if (!string.IsNullOrEmpty(queue.ForwardTo))
        {
            await RouteInternalAsync(queue.ForwardTo, encoded, expiresAt, scheduledFor,
                sessionId, messageId, dedupWindow, filterContext, deliveryCount,
                depth + 1, landed, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _store.EnqueueAsync(
            queue.Name, encoded, expiresAt, scheduledFor,
            sessionId, messageId, dedupWindow, deliveryCount, cancellationToken).ConfigureAwait(false);
        landed.Add(queue.Name);
        _activity?.Touch(queue.Name);
    }
}
