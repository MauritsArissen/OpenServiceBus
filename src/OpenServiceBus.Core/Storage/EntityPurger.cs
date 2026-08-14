using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Composes <see cref="IMessageStore.PurgeAsync"/> into entity-level purge operations:
/// a queue with its dead-letter sibling, a subscription's backing queue with its
/// dead-letter sibling, a topic across all of its subscriptions, and the whole broker.
/// Topology, entity settings, and live links are untouched; only stored messages and the
/// per-queue message bookkeeping (locks, session state, dedup history) are removed.
/// </summary>
public sealed class EntityPurger
{
    private readonly IQueueRegistry _queues;
    private readonly IMessageStore _store;
    private readonly ITopicRegistry? _topics;

    public EntityPurger(IQueueRegistry queues, IMessageStore store, ITopicRegistry? topics = null)
    {
        _queues = queues;
        _store = store;
        _topics = topics;
    }

    public async Task<long?> PurgeQueueAsync(string name, bool deadLetterOnly = false, CancellationToken cancellationToken = default)
    {
        if (await _queues.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }
        return await PurgeBackingQueueAsync(name, deadLetterOnly, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> PurgeSubscriptionAsync(string topicName, string subscriptionName, bool deadLetterOnly = false, CancellationToken cancellationToken = default)
    {
        if (_topics is null)
        {
            return null;
        }
        var sub = await _topics.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (sub is null)
        {
            return null;
        }
        return await PurgeBackingQueueAsync(sub.BackingQueueName, deadLetterOnly, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> PurgeTopicAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_topics is null || await _topics.GetTopicAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }
        long purged = 0;
        foreach (var sub in await _topics.ListSubscriptionsAsync(name, cancellationToken).ConfigureAwait(false))
        {
            purged += await PurgeBackingQueueAsync(sub.BackingQueueName, deadLetterOnly: false, cancellationToken).ConfigureAwait(false);
        }
        // The topic's own store queue holds scheduled publishes that have not activated yet.
        purged += await _store.PurgeAsync(name, cancellationToken).ConfigureAwait(false);
        // Same contract as queue purge: dedup bookkeeping goes with the messages, so a
        // reseeded topic accepts previously-seen MessageIds again.
        await _store.ClearTopicDedupHistoryAsync(name, cancellationToken).ConfigureAwait(false);
        return purged;
    }

    public async Task<(long Purged, int Entities)> PurgeAllAsync(CancellationToken cancellationToken = default)
    {
        long purged = 0;
        var entities = 0;
        foreach (var queue in await _queues.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (EntityNames.IsDeadLetterQueue(queue.Name))
            {
                continue;
            }
            purged += await PurgeBackingQueueAsync(queue.Name, deadLetterOnly: false, cancellationToken).ConfigureAwait(false);
            entities++;
        }
        return (purged, entities);
    }

    private async Task<long> PurgeBackingQueueAsync(string backingQueue, bool deadLetterOnly, CancellationToken cancellationToken)
    {
        long purged = 0;
        if (!deadLetterOnly)
        {
            purged += await _store.PurgeAsync(backingQueue, cancellationToken).ConfigureAwait(false);
            purged += await _store.PurgeAsync(backingQueue + EntityNames.TransferDeadLetterSuffix, cancellationToken).ConfigureAwait(false);
        }
        purged += await _store.PurgeAsync(backingQueue + EntityNames.DeadLetterSuffix, cancellationToken).ConfigureAwait(false);
        return purged;
    }
}
