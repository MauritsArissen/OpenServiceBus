using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Tracks topics, subscriptions, and rules, and evaluates filters at publish time to decide
/// which subscriptions receive a copy. Mirrors the shape of <see cref="IQueueRegistry"/> so
/// adapters (AMQP routing, REST CRUD, config loader, the Bicep loader in Phase 4) all see the
/// same surface.
/// </summary>
public interface ITopicRegistry
{
    Task<TopicDescriptor> CreateTopicAsync(TopicDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the descriptor of an existing topic in place. Throws
    /// <see cref="InvalidOperationException"/> when the topic does not exist.
    /// </summary>
    Task<TopicDescriptor> UpdateTopicAsync(TopicDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<TopicDescriptor?> GetTopicAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TopicDescriptor>> ListTopicsAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteTopicAsync(string name, CancellationToken cancellationToken = default);

    Task<SubscriptionDescriptor> CreateSubscriptionAsync(SubscriptionDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the descriptor of an existing subscription in place, mirroring the updated
    /// settings onto its backing queue. Messages are untouched. Throws
    /// <see cref="InvalidOperationException"/> when the subscription does not exist.
    /// </summary>
    Task<SubscriptionDescriptor> UpdateSubscriptionAsync(SubscriptionDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<SubscriptionDescriptor?> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionDescriptor>> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default);
    Task<bool> DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default);

    Task<RuleDescriptor> CreateOrReplaceRuleAsync(RuleDescriptor rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuleDescriptor>> ListRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the backing-queue addresses of every subscription on the topic whose rule set
    /// matches the message. A subscription with no rules matches nothing; a subscription with
    /// rules matches if *any* rule's filter returns true.
    /// </summary>
    IReadOnlyList<string> EvaluateSubscribers(string topicName, MessageFilterContext message);

    event EventHandler<TopicDescriptor> TopicCreated;
    event EventHandler<TopicDescriptor> TopicDeleted;
    event EventHandler<SubscriptionDescriptor> SubscriptionCreated;

    /// <summary>Raised after a subscription's descriptor has been replaced via <see cref="UpdateSubscriptionAsync"/>.</summary>
    event EventHandler<SubscriptionDescriptor> SubscriptionUpdated;
    event EventHandler<SubscriptionDescriptor> SubscriptionDeleted;
}
