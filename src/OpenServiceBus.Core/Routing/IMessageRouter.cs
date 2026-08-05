using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Routing;

/// <summary>
/// Resolves "where should this message actually land?" against the entity registries.
/// Handles two server-side routing concerns above the bare storage layer:
///
///   1. Topic fan-out - a topic isn't a queue, so a send to a topic must evaluate every
///      subscription's rules and enqueue to the matching backing queues.
///   2. Auto-forwarding - a queue or subscription with <c>ForwardTo</c> set is a
///      transparent passthrough: the original destination never accumulates messages, the
///      router redirects to the configured destination, chaining up to <see cref="MaxForwardDepth"/> hops.
///
/// One method covers both. Senders (queue, topic, even the DLQ writers) call
/// <see cref="RouteAsync"/> with the *configured* destination name and let the router work
/// out the actual storage operations. <see cref="MessageFilterContext"/> is only used when
/// the chain passes through a topic; pass <c>null</c> for direct queue sends.
/// </summary>
public interface IMessageRouter
{
    /// <summary>Maximum number of forward hops in a single chain. Matches Azure Service Bus.</summary>
    int MaxForwardDepth => 4;

    /// <summary>
    /// Enqueue <paramref name="encodedMessage"/> at the entity named <paramref name="targetEntityName"/>,
    /// transparently following any auto-forward chain or topic fan-out. Returns the list of
    /// concrete queue names the message landed in (zero, one, or many for topic fan-out).
    /// </summary>
    /// <param name="filterContext">
    /// Required when the chain may traverse a topic so subscription rules can be evaluated.
    /// Pass <c>null</c> only for hops that are guaranteed to be queues.
    /// </param>
    /// <param name="deliveryCount">
    /// Initial delivery count of the enqueued copy. 0 for fresh sends; DLQ writers pass the
    /// count the message had when it was dead-lettered, matching Azure Service Bus where a
    /// moved message keeps its delivery history.
    /// </param>
    /// <param name="forwardSource">
    /// The entity whose <c>ForwardTo</c> (or <c>ForwardDeadLetteredMessagesTo</c>) caused
    /// this call, when the call IS a forward hop. Enables transfer-dead-letter semantics: a
    /// hop that cannot be delivered (missing, disabled, or full target; hop cap exceeded)
    /// lands in this entity's <c>$Transfer/$DeadLetterQueue</c> instead of being dropped.
    /// Null for direct sends, fan-out, and broker-internal DLQ moves.
    /// </param>
    Task<IReadOnlyList<string>> RouteAsync(
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
        CancellationToken cancellationToken = default);
}
