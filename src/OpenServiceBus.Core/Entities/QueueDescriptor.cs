namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Configuration for a single queue entity.
/// Mirrors the relevant subset of <c>Microsoft.ServiceBus.Messaging.QueueDescription</c>
/// so that the official Service Bus emulator's <c>config.json</c> can be loaded directly.
/// Additional fields land milestone by milestone (lock duration, max delivery count,
/// TTL, scheduled-enqueue handling, etc.).
/// </summary>
public sealed record QueueDescriptor
{
    public required string Name { get; init; }

    /// <summary>
    /// Maximum delivery attempts before a message is dead-lettered. Enforced.
    /// Default matches Azure Service Bus.
    /// </summary>
    public int MaxDeliveryCount { get; init; } = 10;

    /// <summary>
    /// Peek-lock duration handed to consumers when a message is delivered. Enforced.
    /// Default matches Azure Service Bus.
    /// </summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether expired messages are dead-lettered or dropped. Enforced.
    /// </summary>
    public bool DeadLetteringOnMessageExpiration { get; init; }

    /// <summary>
    /// Per-queue default message TTL. Per-message TTL still wins when shorter. Enforced.
    /// </summary>
    public TimeSpan? DefaultMessageTimeToLive { get; init; }

    /// <summary>
    /// When true, the queue is session-enabled: messages must carry a <c>SessionId</c>
    /// (AMQP <c>group-id</c>) and receivers must attach with a session filter. Enforced.
    /// </summary>
    public bool RequiresSession { get; init; }

    /// <summary>
    /// When true, the broker silently drops repeat sends within
    /// <see cref="DuplicateDetectionHistoryTimeWindow"/> based on the message's
    /// <c>MessageId</c>. Mirrors Azure Service Bus - duplicates are not surfaced to the
    /// sender; the SDK still gets an "accepted" disposition. Enforced.
    /// </summary>
    public bool RequiresDuplicateDetection { get; init; }

    /// <summary>
    /// Sliding-window duration during which two sends with the same <c>MessageId</c> are
    /// treated as duplicates. Only honoured when <see cref="RequiresDuplicateDetection"/>
    /// is true. Defaults to 10 minutes when null - matches Azure Service Bus.
    /// </summary>
    public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; init; }

    /// <summary>
    /// Destination entity (queue or topic by name) that every accepted send is transparently
    /// forwarded to. Senders see a normal "accepted" disposition; no message ever lands on
    /// this queue itself. Enforced server-side with a 4-hop chain cap matching Azure
    /// Service Bus. Null disables auto-forwarding.
    /// </summary>
    public string? ForwardTo { get; init; }

    /// <summary>
    /// Destination entity that dead-lettered messages are forwarded to instead of the
    /// queue's local <c>$DeadLetterQueue</c>. Applies to every DLQ trigger - explicit
    /// dead-letter, max-delivery, TTL expiration. Enforced. Null = standard local DLQ.
    /// </summary>
    public string? ForwardDeadLetteredMessagesTo { get; init; }

    /// <summary>
    /// Free-form metadata attached by management clients (the SDK's <c>UserMetadata</c>).
    /// Carried and returned verbatim; no broker semantics.
    /// </summary>
    public string? UserMetadata { get; init; }

    /// <summary>When the entity was created. Reported through the management API.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the entity was last updated, or null if never updated after creation.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
