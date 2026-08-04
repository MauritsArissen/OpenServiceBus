namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Configuration for a single topic entity. Topics themselves don't store messages - they're
/// fan-out endpoints. Each subscription on the topic owns its own message store, peek-lock
/// lifecycle, and DLQ (see <see cref="SubscriptionDescriptor"/>).
/// </summary>
public sealed record TopicDescriptor
{
    public required string Name { get; init; }

    /// <summary>Per-topic default message TTL; applies to messages enqueued without their own TTL.</summary>
    public TimeSpan? DefaultMessageTimeToLive { get; init; }

    /// <summary>
    /// Free-form metadata attached by management clients (the SDK's <c>UserMetadata</c>).
    /// Carried and returned verbatim; no broker semantics.
    /// </summary>
    public string? UserMetadata { get; init; }

    /// <summary>Operational status. Enforced on the publish path.</summary>
    public EntityStatus Status { get; init; } = EntityStatus.Active;

    /// <summary>When the entity was created. Reported through the management API.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the entity was last updated, or null if never updated after creation.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
