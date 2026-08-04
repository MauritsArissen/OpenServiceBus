namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Configuration for a single subscription on a topic. Storage-wise a subscription is just a
/// queue named <c>&lt;topic&gt;/subscriptions/&lt;name&gt;</c>; receivers attach to that address
/// and pick up messages with full peek-lock semantics.
/// </summary>
public sealed record SubscriptionDescriptor
{
    public required string TopicName { get; init; }
    public required string Name { get; init; }

    public int MaxDeliveryCount { get; init; } = 10;

    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(60);

    public bool DeadLetteringOnMessageExpiration { get; init; }

    public TimeSpan? DefaultMessageTimeToLive { get; init; }

    /// <summary>Session-enabled subscription. See <see cref="QueueDescriptor.RequiresSession"/>.</summary>
    public bool RequiresSession { get; init; }

    /// <summary>
    /// Auto-forward target for messages that match this subscription's rules.
    /// See <see cref="QueueDescriptor.ForwardTo"/>. Enforced.
    /// </summary>
    public string? ForwardTo { get; init; }

    /// <summary>
    /// Auto-forward target for dead-lettered messages on this subscription.
    /// See <see cref="QueueDescriptor.ForwardDeadLetteredMessagesTo"/>. Enforced.
    /// </summary>
    public string? ForwardDeadLetteredMessagesTo { get; init; }

    /// <summary>
    /// Free-form metadata attached by management clients (the SDK's <c>UserMetadata</c>).
    /// Carried and returned verbatim; no broker semantics.
    /// </summary>
    public string? UserMetadata { get; init; }

    /// <summary>Operational status. Enforced on fan-out and the receive path.</summary>
    public EntityStatus Status { get; init; } = EntityStatus.Active;

    /// <summary>When the entity was created. Reported through the management API.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the entity was last updated, or null if never updated after creation.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// The backing queue address: <c>&lt;TopicName&gt;/subscriptions/&lt;Name&gt;</c>.
    /// This is what AMQP receivers attach to and what the in-memory store keys on.
    /// </summary>
    public string BackingQueueName => EntityNames.SubscriptionAddress(TopicName, Name);
}
