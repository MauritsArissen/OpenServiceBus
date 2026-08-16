using System.Collections.Concurrent;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.Topics;

/// <summary>
/// In-memory <see cref="ITopicRegistry"/>. Each subscription is modelled as a regular queue
/// (created via <see cref="IQueueRegistry"/>) named <c>&lt;topic&gt;/Subscriptions/&lt;sub&gt;</c>;
/// that reuses every queue feature the broker already has - peek-lock, DLQ, lock renewal,
/// TTL, scheduled messages, defer, dead-letter, etc.
/// </summary>
public sealed class TopicManager : ITopicRegistry
{
    /// <summary>Service Bus auto-installs this rule with a <see cref="TrueFilter"/> on every fresh subscription.</summary>
    public const string DefaultRuleName = "$Default";

    private readonly IQueueRegistry _queues;
    private readonly IMessageStore? _store;

    private readonly ConcurrentDictionary<string, TopicDescriptor> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SubscriptionDescriptor> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RuleDescriptor>> _rules = new(StringComparer.OrdinalIgnoreCase);

    // The store is optional so unit tests can construct a bare registry; when present it
    // persists topic descriptor snapshots (restart survival on SQLite) and owns the
    // topic-level dedup history that must die with the topic.
    public TopicManager(IQueueRegistry queues, IMessageStore? store = null)
    {
        _queues = queues;
        _store = store;
    }

    public event EventHandler<TopicDescriptor>? TopicCreated;
    public event EventHandler<TopicDescriptor>? TopicUpdated;
    public event EventHandler<TopicDescriptor>? TopicDeleted;
    public event EventHandler<SubscriptionDescriptor>? SubscriptionCreated;
    public event EventHandler<SubscriptionDescriptor>? SubscriptionUpdated;
    public event EventHandler<SubscriptionDescriptor>? SubscriptionDeleted;

    event EventHandler<TopicDescriptor> ITopicRegistry.TopicCreated { add => TopicCreated += value; remove => TopicCreated -= value; }
    event EventHandler<TopicDescriptor> ITopicRegistry.TopicUpdated { add => TopicUpdated += value; remove => TopicUpdated -= value; }
    event EventHandler<TopicDescriptor> ITopicRegistry.TopicDeleted { add => TopicDeleted += value; remove => TopicDeleted -= value; }
    event EventHandler<SubscriptionDescriptor> ITopicRegistry.SubscriptionCreated { add => SubscriptionCreated += value; remove => SubscriptionCreated -= value; }
    event EventHandler<SubscriptionDescriptor> ITopicRegistry.SubscriptionUpdated { add => SubscriptionUpdated += value; remove => SubscriptionUpdated -= value; }
    event EventHandler<SubscriptionDescriptor> ITopicRegistry.SubscriptionDeleted { add => SubscriptionDeleted += value; remove => SubscriptionDeleted -= value; }

    public async Task<TopicDescriptor> CreateTopicAsync(TopicDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        EntityValidation.EnsureAutoDeleteOnIdle(descriptor.AutoDeleteOnIdle, descriptor.Name);
        var stored = _topics.GetOrAdd(descriptor.Name, descriptor);
        if (ReferenceEquals(stored, descriptor))
        {
            if (_store is not null)
            {
                // The topic's own store queue holds SCHEDULED publishes until activation
                // (immediate publishes fan out without ever touching it).
                await _store.CreateQueueAsync(descriptor.Name, cancellationToken).ConfigureAwait(false);
                await _store.SaveTopicDescriptorAsync(descriptor.Name, TopicDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
            }
            TopicCreated?.Invoke(this, descriptor);
        }
        return stored;
    }

    public async Task<TopicDescriptor> UpdateTopicAsync(TopicDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        EntityValidation.EnsureAutoDeleteOnIdle(descriptor.AutoDeleteOnIdle, descriptor.Name);

        while (true)
        {
            if (!_topics.TryGetValue(descriptor.Name, out var existing))
            {
                throw new InvalidOperationException($"Topic '{descriptor.Name}' does not exist.");
            }
            if (_topics.TryUpdate(descriptor.Name, descriptor, existing))
            {
                if (_store is not null)
                {
                    await _store.SaveTopicDescriptorAsync(descriptor.Name, TopicDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
                }
                TopicUpdated?.Invoke(this, descriptor);
                return descriptor;
            }
        }
    }

    public Task<TopicDescriptor?> GetTopicAsync(string name, CancellationToken cancellationToken = default)
    {
        _topics.TryGetValue(name, out var topic);
        return Task.FromResult(topic);
    }

    public Task<IReadOnlyList<TopicDescriptor>> ListTopicsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TopicDescriptor> snapshot = _topics.Values.ToArray();
        return Task.FromResult(snapshot);
    }

    public async Task<bool> DeleteTopicAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_topics.TryRemove(name, out var topic))
        {
            return false;
        }
        if (_store is not null)
        {
            await _store.DeleteQueueAsync(name, cancellationToken).ConfigureAwait(false);
            await _store.DeleteTopicDescriptorAsync(name, cancellationToken).ConfigureAwait(false);
            await _store.ClearTopicDedupHistoryAsync(name, cancellationToken).ConfigureAwait(false);
        }
        TopicDeleted?.Invoke(this, topic);

        // Tear down all subscriptions on this topic. Snapshot first so we mutate safely.
        var subKeys = _subscriptions.Keys.Where(k => k.StartsWith($"{name}/", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var key in subKeys)
        {
            if (_subscriptions.TryGetValue(key, out var sub))
            {
                await DeleteSubscriptionAsync(sub.TopicName, sub.Name, cancellationToken).ConfigureAwait(false);
            }
        }
        return true;
    }

    public async Task<SubscriptionDescriptor> CreateSubscriptionAsync(SubscriptionDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        EntityValidation.EnsureAutoDeleteOnIdle(descriptor.AutoDeleteOnIdle, $"{descriptor.TopicName}/{descriptor.Name}");
        if (!_topics.ContainsKey(descriptor.TopicName))
        {
            throw new InvalidOperationException($"Cannot create subscription '{descriptor.Name}' - topic '{descriptor.TopicName}' does not exist.");
        }

        // Self-forwarding rejection - also catches "forwards to my own backing queue" since
        // that's the same entity from the router's point of view.
        if (!string.IsNullOrEmpty(descriptor.ForwardTo)
            && (string.Equals(descriptor.ForwardTo, descriptor.BackingQueueName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(descriptor.ForwardTo, descriptor.TopicName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Subscription '{descriptor.TopicName}/{descriptor.Name}' cannot forward to itself or its parent topic.");
        }
        if (!string.IsNullOrEmpty(descriptor.ForwardDeadLetteredMessagesTo)
            && string.Equals(descriptor.ForwardDeadLetteredMessagesTo, descriptor.BackingQueueName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Subscription '{descriptor.TopicName}/{descriptor.Name}' cannot forward dead-lettered messages to itself.");
        }

        var key = SubKey(descriptor.TopicName, descriptor.Name);
        var stored = _subscriptions.GetOrAdd(key, descriptor);
        if (!ReferenceEquals(stored, descriptor))
        {
            return stored;
        }

        // The backing queue gives us all the queue-level machinery for free.
        // Mirror ForwardDeadLetteredMessagesTo onto the backing queue so the receiver
        // sources (which key off the queue descriptor) honor it. Subscription-level ForwardTo
        // is enforced one level up - in the topic fan-out - so it stays on the descriptor only.
        await _queues.CreateAsync(new QueueDescriptor
        {
            Name = descriptor.BackingQueueName,
            LockDuration = descriptor.LockDuration,
            MaxDeliveryCount = descriptor.MaxDeliveryCount,
            DefaultMessageTimeToLive = descriptor.DefaultMessageTimeToLive,
            DeadLetteringOnMessageExpiration = descriptor.DeadLetteringOnMessageExpiration,
            ForwardDeadLetteredMessagesTo = descriptor.ForwardDeadLetteredMessagesTo,
            Status = descriptor.Status,
        }, cancellationToken).ConfigureAwait(false);

        // Every fresh subscription gets a $Default rule with a TrueFilter - same as Azure SB.
        var rules = _rules.GetOrAdd(key, _ => new ConcurrentDictionary<string, RuleDescriptor>(StringComparer.OrdinalIgnoreCase));
        var defaultRule = new RuleDescriptor
        {
            TopicName = descriptor.TopicName,
            SubscriptionName = descriptor.Name,
            Name = DefaultRuleName,
            Filter = TrueFilter.Instance,
        };
        rules[DefaultRuleName] = defaultRule;

        if (_store is not null)
        {
            await _store.SaveSubscriptionDescriptorAsync(
                descriptor.TopicName, descriptor.Name, SubscriptionDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
            await _store.SaveSubscriptionRuleAsync(
                descriptor.TopicName, descriptor.Name, DefaultRuleName, RuleDescriptorJson.Serialize(defaultRule), cancellationToken).ConfigureAwait(false);
        }

        SubscriptionCreated?.Invoke(this, descriptor);
        return descriptor;
    }

    public async Task<SubscriptionDescriptor> UpdateSubscriptionAsync(SubscriptionDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.TopicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        EntityValidation.EnsureAutoDeleteOnIdle(descriptor.AutoDeleteOnIdle, $"{descriptor.TopicName}/{descriptor.Name}");

        var key = SubKey(descriptor.TopicName, descriptor.Name);
        while (true)
        {
            if (!_subscriptions.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException($"Subscription '{descriptor.TopicName}/{descriptor.Name}' does not exist.");
            }
            if (_subscriptions.TryUpdate(key, descriptor, existing))
            {
                break;
            }
        }

        // Mirror the same settings onto the backing queue that create mirrors, so the
        // receiver sources (which key off the queue descriptor) pick up the new values.
        var backing = await _queues.GetAsync(descriptor.BackingQueueName, cancellationToken).ConfigureAwait(false);
        if (backing is not null)
        {
            await _queues.UpdateAsync(backing with
            {
                LockDuration = descriptor.LockDuration,
                MaxDeliveryCount = descriptor.MaxDeliveryCount,
                DefaultMessageTimeToLive = descriptor.DefaultMessageTimeToLive,
                DeadLetteringOnMessageExpiration = descriptor.DeadLetteringOnMessageExpiration,
                ForwardDeadLetteredMessagesTo = descriptor.ForwardDeadLetteredMessagesTo,
                Status = descriptor.Status,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (_store is not null)
        {
            await _store.SaveSubscriptionDescriptorAsync(
                descriptor.TopicName, descriptor.Name, SubscriptionDescriptorJson.Serialize(descriptor), cancellationToken).ConfigureAwait(false);
        }

        SubscriptionUpdated?.Invoke(this, descriptor);
        return descriptor;
    }

    public Task<SubscriptionDescriptor?> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
    {
        _subscriptions.TryGetValue(SubKey(topicName, subscriptionName), out var sub);
        return Task.FromResult(sub);
    }

    public Task<IReadOnlyList<SubscriptionDescriptor>> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default)
    {
        var prefix = $"{topicName}/";
        IReadOnlyList<SubscriptionDescriptor> snapshot = _subscriptions
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToArray();
        return Task.FromResult(snapshot);
    }

    public async Task<bool> DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
    {
        var key = SubKey(topicName, subscriptionName);
        if (!_subscriptions.TryRemove(key, out var sub))
        {
            return false;
        }
        _rules.TryRemove(key, out _);
        await _queues.DeleteAsync(sub.BackingQueueName, cancellationToken).ConfigureAwait(false);
        if (_store is not null)
        {
            // Cascades the persisted rule snapshots with it.
            await _store.DeleteSubscriptionDescriptorAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        }
        SubscriptionDeleted?.Invoke(this, sub);
        return true;
    }

    public async Task<RuleDescriptor> CreateOrReplaceRuleAsync(RuleDescriptor rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var key = SubKey(rule.TopicName, rule.SubscriptionName);
        if (!_subscriptions.ContainsKey(key))
        {
            throw new InvalidOperationException($"Cannot add rule to '{rule.TopicName}/{rule.SubscriptionName}' - subscription does not exist.");
        }
        var rules = _rules.GetOrAdd(key, _ => new ConcurrentDictionary<string, RuleDescriptor>(StringComparer.OrdinalIgnoreCase));
        rules[rule.Name] = rule;
        if (_store is not null)
        {
            await _store.SaveSubscriptionRuleAsync(
                rule.TopicName, rule.SubscriptionName, rule.Name, RuleDescriptorJson.Serialize(rule), cancellationToken).ConfigureAwait(false);
        }
        return rule;
    }

    public async Task<bool> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default)
    {
        var key = SubKey(topicName, subscriptionName);
        if (!_rules.TryGetValue(key, out var rules) || !rules.TryRemove(ruleName, out _))
        {
            return false;
        }
        if (_store is not null)
        {
            await _store.DeleteSubscriptionRuleAsync(topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public Task<IReadOnlyList<RuleDescriptor>> ListRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
    {
        var key = SubKey(topicName, subscriptionName);
        if (!_rules.TryGetValue(key, out var rules))
        {
            return Task.FromResult<IReadOnlyList<RuleDescriptor>>(Array.Empty<RuleDescriptor>());
        }
        IReadOnlyList<RuleDescriptor> snapshot = rules.Values.ToArray();
        return Task.FromResult(snapshot);
    }

    public IReadOnlyList<string> EvaluateSubscribers(string topicName, MessageFilterContext message) =>
        EvaluateSubscriberMatches(topicName, message).Select(m => m.Subscription.BackingQueueName).ToArray();

    public IReadOnlyList<SubscriberMatch> EvaluateSubscriberMatches(string topicName, MessageFilterContext message)
    {
        if (!_topics.ContainsKey(topicName)) return Array.Empty<SubscriberMatch>();

        var prefix = $"{topicName}/";
        var matched = new List<SubscriberMatch>();
        foreach (var (key, sub) in _subscriptions)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_rules.TryGetValue(key, out var rules) || rules.IsEmpty) continue;

            // Rule name order keeps "which rule's action applies" deterministic when
            // several rules match (the dictionary itself has no stable order).
            foreach (var rule in rules.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                // A filter that throws at evaluation time (e.g. arithmetic on a string
                // property) counts as a non-match for this subscription, like real
                // Service Bus - it must never fail the publish itself.
                bool matches;
                try
                {
                    matches = rule.Filter.Matches(message);
                }
                catch (InvalidOperationException)
                {
                    matches = false;
                }
                if (matches)
                {
                    matched.Add(new SubscriberMatch(sub, rule.Action));
                    break;
                }
            }
        }
        return matched;
    }

    private static string SubKey(string topic, string sub) => $"{topic}/{sub}";
}
