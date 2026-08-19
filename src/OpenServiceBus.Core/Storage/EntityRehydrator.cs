using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Rebuilds the in-memory registries from a persistent <see cref="IMessageStore"/>. Only
/// meaningful with a persistent store (SQLite): on restart the backing file still holds the
/// entity rows but the registries are empty memory.
///
/// Restore order matters. Topics come back from their own snapshots first, then subscriptions
/// from theirs (full fidelity - sessions, forwarding, auto-delete, metadata), then rules, and
/// only then the legacy backing-queue scan picks up anything a pre-existing database has no
/// subscription snapshot for. Whatever already exists in the registry wins: the config
/// bootstrap runs before this and stays the declarative source of truth.
///
/// The algorithm lives here rather than in the host so it can be driven directly from tests.
/// </summary>
public sealed class EntityRehydrator
{
    private readonly IMessageStore _store;
    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry? _topics;
    private readonly Action<string, Exception>? _onError;

    /// <param name="onError">
    /// Invoked with a human-readable context string when a single entity fails to come back.
    /// One bad row must not abort the rest of the rehydration, so failures are reported and
    /// skipped rather than thrown.
    /// </param>
    public EntityRehydrator(
        IMessageStore store,
        IQueueRegistry queues,
        ITopicRegistry? topics = null,
        Action<string, Exception>? onError = null)
    {
        _store = store;
        _queues = queues;
        _topics = topics;
        _onError = onError;
    }

    public async Task<RehydrationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var knownQueues = (await _queues.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(q => q.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedDescriptors = _store.LoadQueueDescriptors();
        var savedTopics = _store.LoadTopicDescriptors();
        var savedSubscriptions = _store.LoadSubscriptionDescriptors();
        var savedRules = _store.LoadSubscriptionRules();

        var queues = 0;
        var topics = 0;
        var subscriptions = 0;

        // Subscriptions this run brought back, and which therefore own their persisted rules.
        // A subscription the config bootstrap already declared is left exactly as declared.
        var restored = new List<string>();

        if (_topics is not null)
        {
            foreach (var (topicName, json) in savedTopics)
            {
                try
                {
                    if (await _topics.GetTopicAsync(topicName, cancellationToken).ConfigureAwait(false) is not null) continue;
                    var descriptor = TopicDescriptorJson.Deserialize(json) ?? new TopicDescriptor { Name = topicName };
                    await _topics.CreateTopicAsync(descriptor with { Name = topicName }, cancellationToken).ConfigureAwait(false);
                    topics++;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke($"topic '{topicName}'", ex);
                }
            }

            foreach (var (address, json) in savedSubscriptions)
            {
                if (!EntityNames.TryParseSubscriptionAddress(address, out var topicName, out var subName)) continue;
                try
                {
                    if (await _topics.GetSubscriptionAsync(topicName, subName, cancellationToken).ConfigureAwait(false) is not null) continue;
                    if (await _topics.GetTopicAsync(topicName, cancellationToken).ConfigureAwait(false) is null)
                    {
                        await _topics.CreateTopicAsync(new TopicDescriptor { Name = topicName }, cancellationToken).ConfigureAwait(false);
                        topics++;
                    }

                    var descriptor = SubscriptionDescriptorJson.Deserialize(json)
                        ?? new SubscriptionDescriptor { TopicName = topicName, Name = subName };
                    await _topics.CreateSubscriptionAsync(
                        descriptor with { TopicName = topicName, Name = subName }, cancellationToken).ConfigureAwait(false);
                    subscriptions++;
                    restored.Add(address);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke($"subscription '{topicName}/{subName}'", ex);
                }
            }
        }

        foreach (var name in _store.ListQueueNames())
        {
            // The DLQ sibling is created automatically alongside its parent (queue or subscription),
            // so never discover it directly - that would double-register or fight the auto-sibling logic.
            if (name.EndsWith(EntityNames.DeadLetterSuffix, StringComparison.Ordinal)) continue;

            // Subscription backing queue with no subscription snapshot of its own - a database
            // written before subscription descriptors were persisted. Reconstruct what the
            // backing queue can tell us so the pub/sub layer comes back rather than resurfacing
            // as an orphan plain queue.
            if (EntityNames.TryParseSubscriptionAddress(name, out var scanTopic, out var scanSub))
            {
                if (_topics is null) continue;

                try
                {
                    if (await _topics.GetTopicAsync(scanTopic, cancellationToken).ConfigureAwait(false) is null)
                    {
                        await _topics.CreateTopicAsync(new TopicDescriptor { Name = scanTopic }, cancellationToken).ConfigureAwait(false);
                        topics++;
                    }

                    if (await _topics.GetSubscriptionAsync(scanTopic, scanSub, cancellationToken).ConfigureAwait(false) is null)
                    {
                        // The persisted backing-queue snapshot restores the settings the backing
                        // queue mirrors; the subscription-only ones stay at their defaults.
                        var descriptor = new SubscriptionDescriptor { TopicName = scanTopic, Name = scanSub };
                        if (savedDescriptors.TryGetValue(name, out var backingJson)
                            && QueueDescriptorJson.Deserialize(backingJson) is { } backing)
                        {
                            descriptor = descriptor with
                            {
                                LockDuration = backing.LockDuration,
                                MaxDeliveryCount = backing.MaxDeliveryCount,
                                DefaultMessageTimeToLive = backing.DefaultMessageTimeToLive,
                                DeadLetteringOnMessageExpiration = backing.DeadLetteringOnMessageExpiration,
                                ForwardDeadLetteredMessagesTo = backing.ForwardDeadLetteredMessagesTo,
                                Status = backing.Status,
                            };
                        }
                        await _topics.CreateSubscriptionAsync(descriptor, cancellationToken).ConfigureAwait(false);
                        subscriptions++;
                        restored.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    _onError?.Invoke($"subscription '{scanTopic}/{scanSub}'", ex);
                }
                continue;
            }

            // A store queue named after a topic is that topic's scheduled-publish holding
            // queue, not a user queue - the topic itself was already restored above.
            if (_topics is not null
                && await _topics.GetTopicAsync(name, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            // Plain queue - restored from its persisted descriptor snapshot when one exists.
            if (knownQueues.Contains(name)) continue;
            var queueDescriptor = savedDescriptors.TryGetValue(name, out var json)
                ? QueueDescriptorJson.Deserialize(json) ?? new QueueDescriptor { Name = name }
                : new QueueDescriptor { Name = name };
            await _queues.CreateAsync(queueDescriptor with { Name = name }, cancellationToken).ConfigureAwait(false);
            queues++;
        }

        var rules = _topics is null
            ? 0
            : await RestoreRulesAsync(restored, savedRules, cancellationToken).ConfigureAwait(false);

        return new RehydrationResult(queues, topics, subscriptions, rules);
    }

    /// <summary>
    /// Reinstate the persisted rule set on every subscription this run brought back. The set
    /// replaces rather than merges: a fresh subscription is born with a <c>$Default</c>
    /// TrueFilter, and leaving that in place next to the restored rules would silently widen
    /// the subscription back to match-all - the exact failure this whole path exists to fix.
    /// </summary>
    private async Task<int> RestoreRulesAsync(
        IReadOnlyList<string> restored,
        IReadOnlyDictionary<string, IReadOnlyList<string>> savedRules,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var address in restored)
        {
            if (!savedRules.TryGetValue(address, out var snapshots) || snapshots.Count == 0) continue;
            if (!EntityNames.TryParseSubscriptionAddress(address, out var topicName, out var subName)) continue;

            var parsed = snapshots
                .Select(RuleDescriptorJson.Deserialize)
                .Where(r => r is not null)
                .Select(r => r! with { TopicName = topicName, SubscriptionName = subName })
                .ToArray();
            if (parsed.Length == 0) continue;

            try
            {
                foreach (var existing in await _topics!.ListRulesAsync(topicName, subName, cancellationToken).ConfigureAwait(false))
                {
                    if (parsed.Any(r => string.Equals(r.Name, existing.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    await _topics.DeleteRuleAsync(topicName, subName, existing.Name, cancellationToken).ConfigureAwait(false);
                }

                foreach (var rule in parsed)
                {
                    await _topics.CreateOrReplaceRuleAsync(rule, cancellationToken).ConfigureAwait(false);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _onError?.Invoke($"rules for subscription '{topicName}/{subName}'", ex);
            }
        }
        return count;
    }
}

/// <summary>How many entities of each kind <see cref="EntityRehydrator"/> brought back.</summary>
public readonly record struct RehydrationResult(int Queues, int Topics, int Subscriptions, int Rules)
{
    /// <summary>True when nothing needed restoring - the common case for a fresh store.</summary>
    public bool IsEmpty => Queues == 0 && Topics == 0 && Subscriptions == 0 && Rules == 0;
}
