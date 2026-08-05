namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Service Bus entity name conventions - the suffixes and helpers used to reason about
/// sub-entities of a queue or topic (dead-letter queues, management endpoints, subscriptions).
/// Lives in Core because every adapter - AMQP routing, REST CRUD, future config loader -
/// needs to recognise these patterns.
/// </summary>
public static class EntityNames
{
    /// <summary>The suffix Service Bus uses for the dead-letter sub-entity of a queue.</summary>
    public const string DeadLetterSuffix = "/$DeadLetterQueue";

    /// <summary>The suffix for the transfer dead-letter sub-entity - where messages land when
    /// their auto-forward hop cannot be delivered.</summary>
    public const string TransferDeadLetterSuffix = "/$Transfer/$DeadLetterQueue";

    /// <summary>The suffix for the per-entity AMQP <c>$management</c> request/response node.</summary>
    public const string ManagementSuffix = "/$management";

    /// <summary>The segment that introduces a subscription path: <c>&lt;topic&gt;/Subscriptions/&lt;sub&gt;</c>.</summary>
    public const string SubscriptionsSegment = "/Subscriptions/";

    /// <summary>The address of the broker-wide Claims-Based Security node.</summary>
    public const string CbsAddress = "$cbs";

    /// <summary>True when the given name identifies a dead-letter sub-entity. Transfer
    /// dead-letter names share the terminal suffix, so they match too - both are internal
    /// sub-entities with identical delivery semantics.</summary>
    public static bool IsDeadLetterQueue(string name) =>
        name.EndsWith(DeadLetterSuffix, StringComparison.Ordinal);

    /// <summary>True when the given name identifies a transfer dead-letter sub-entity.</summary>
    public static bool IsTransferDeadLetterQueue(string name) =>
        name.EndsWith(TransferDeadLetterSuffix, StringComparison.Ordinal);

    /// <summary>The canonical backing-queue address for a subscription on a topic.</summary>
    public static string SubscriptionAddress(string topicName, string subscriptionName) =>
        $"{topicName}{SubscriptionsSegment}{subscriptionName}";
}
