namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Parses a Service Bus entity address into the parent entity name and the sub-resource it targets.
/// Examples:
///   <c>orders</c>                                                → Queue main
///   <c>orders/$DeadLetterQueue</c>                               → Queue DLQ
///   <c>orders/$management</c>                                    → Queue $management
///   <c>events/Subscriptions/billing</c>                          → Subscription main
///   <c>events/Subscriptions/billing/$DeadLetterQueue</c>         → Subscription DLQ
///   <c>events/Subscriptions/billing/$management</c>              → Subscription $management
///   <c>events/$management</c>                                    → Topic-level $management (for rule ops)
///
/// Lives in Core because both the AMQP link router and the REST management surface need to
/// recognise these patterns.
/// </summary>
public readonly record struct EntityAddress(
    EntityKind Kind,
    string Entity,
    string? Subscription,
    EntitySubResource SubResource)
{
    /// <summary>Convenience: the canonical backing-queue name for this address (DLQ-suffixed when applicable).</summary>
    public string BackingQueueName => SubResource switch
    {
        EntitySubResource.DeadLetterQueue => $"{StorageBaseName}{EntityNames.DeadLetterSuffix}",
        EntitySubResource.TransferDeadLetterQueue => $"{StorageBaseName}{EntityNames.TransferDeadLetterSuffix}",
        _ => StorageBaseName,
    };

    /// <summary>The base storage entity (without DLQ suffix): a queue name or a subscription backing queue.</summary>
    public string StorageBaseName => Kind == EntityKind.Subscription
        ? EntityNames.SubscriptionAddress(Entity, Subscription!)
        : Entity;

    public static bool TryParse(string? address, out EntityAddress result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(address)) return false;

        // The Azure SDK prefixes entity addresses with "/" (e.g. "/orders") even though our
        // registry stores them as "orders". Strip the leading slash so both AMQPNetLite-style
        // and SDK-style addresses resolve identically.
        var normalized = address.TrimStart('/');
        if (string.IsNullOrEmpty(normalized)) return false;

        // Detect and peel off a known terminal sub-resource first (transfer DLQ before the
        // plain DLQ - they share the terminal suffix - then $management). Matching is
        // case-insensitive like Azure: the Java SDK addresses the transfer DLQ as
        // "$Transfer/$deadletterqueue" where the other stacks use "$DeadLetterQueue".
        // BackingQueueName recomposes with the canonical constants, so storage lookups
        // stay canonical regardless of the client's casing.
        var subResource = EntitySubResource.Main;
        if (normalized.EndsWith(EntityNames.TransferDeadLetterSuffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^EntityNames.TransferDeadLetterSuffix.Length];
            subResource = EntitySubResource.TransferDeadLetterQueue;
        }
        else if (normalized.EndsWith(EntityNames.DeadLetterSuffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^EntityNames.DeadLetterSuffix.Length];
            subResource = EntitySubResource.DeadLetterQueue;
        }
        else if (normalized.EndsWith(EntityNames.ManagementSuffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^EntityNames.ManagementSuffix.Length];
            subResource = EntitySubResource.Management;
        }
        if (string.IsNullOrEmpty(normalized)) return false;

        // Subscription path: <topic>/Subscriptions/<sub>. Case-insensitive on the segment.
        var subsIdx = normalized.IndexOf(EntityNames.SubscriptionsSegment, StringComparison.OrdinalIgnoreCase);
        if (subsIdx > 0)
        {
            var topic = normalized[..subsIdx];
            var sub = normalized[(subsIdx + EntityNames.SubscriptionsSegment.Length)..];
            if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(sub)) return false;

            // The subscription segment is the only "/" allowed in the remaining name; reject deeper paths.
            if (sub.Contains('/', StringComparison.Ordinal)) return false;

            result = new EntityAddress(EntityKind.Subscription, topic, sub, subResource);
            return true;
        }

        if (normalized.Contains('/', StringComparison.Ordinal)) return false;
        result = new EntityAddress(EntityKind.Queue, normalized, null, subResource);
        return true;
    }
}

public enum EntityKind
{
    Queue,
    Subscription,
}

public enum EntitySubResource
{
    Main,
    DeadLetterQueue,
    TransferDeadLetterQueue,
    Management,
}
