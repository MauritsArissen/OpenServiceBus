using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Entities;

/// <summary>
/// A single rule attached to a subscription. The filter decides whether a message published
/// to the parent topic flows into the subscription; the optional action mutates that
/// subscription's copy before it lands.
/// </summary>
public sealed record RuleDescriptor
{
    public required string SubscriptionName { get; init; }
    public required string TopicName { get; init; }

    /// <summary>Service Bus's default rule on a fresh subscription is named <c>$Default</c>.</summary>
    public required string Name { get; init; }

    public required RuleFilter Filter { get; init; }

    /// <summary>
    /// Optional SQL action (<c>SET</c>/<c>REMOVE</c> statements) applied to the matched
    /// subscription's copy during fan-out. Null = no mutation (the common case).
    /// </summary>
    public SqlRuleAction? Action { get; init; }

    /// <summary>Backing-queue address of the owning subscription.</summary>
    public string BackingQueueName => EntityNames.SubscriptionAddress(TopicName, SubscriptionName);
}
