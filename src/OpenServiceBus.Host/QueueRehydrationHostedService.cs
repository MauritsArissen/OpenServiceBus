using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Host;

/// <summary>
/// After config bootstrap, ensure the in-memory registries have an entry for every entity the
/// backing <see cref="IMessageStore"/> knows about. Only relevant with a persistent store
/// (SQLite): on restart the SQLite file still holds the queue rows but the registries are empty
/// memory.
///
/// Plain queues rehydrate as <see cref="QueueDescriptor"/>s; subscription backing queues
/// (<c>&lt;topic&gt;/Subscriptions/&lt;sub&gt;</c>) rehydrate the topic + subscription through the
/// <see cref="ITopicRegistry"/> so senders can attach to the topic and fan-out works again.
/// Per-entity settings and custom subscription rules are NOT persisted, so rehydrated entities
/// come back with default settings and a <c>$Default</c> TrueFilter; declare topology in
/// <c>config.json</c> (which runs before this service) when you need it to survive exactly.
/// </summary>
public sealed class QueueRehydrationHostedService : IHostedService
{
    private readonly IMessageStore _store;
    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry? _topics;
    private readonly ILogger<QueueRehydrationHostedService> _logger;

    public QueueRehydrationHostedService(
        IMessageStore store,
        IQueueRegistry queues,
        ILogger<QueueRehydrationHostedService> logger,
        ITopicRegistry? topics = null)
    {
        _store = store;
        _queues = queues;
        _topics = topics;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var knownQueues = (await _queues.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(q => q.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rehydratedQueues = 0;
        var rehydratedSubscriptions = 0;

        foreach (var name in _store.ListQueueNames())
        {
            // The DLQ sibling is created automatically alongside its parent (queue or subscription),
            // so never discover it directly - that would double-register or fight the auto-sibling logic.
            if (name.EndsWith(EntityNames.DeadLetterSuffix, StringComparison.Ordinal)) continue;

            // Subscription backing queue: reconstruct the topic + subscription so the pub/sub layer
            // (which is not persisted) comes back rather than resurfacing as an orphan plain queue.
            var subsIdx = name.IndexOf(EntityNames.SubscriptionsSegment, StringComparison.OrdinalIgnoreCase);
            if (subsIdx > 0)
            {
                if (_topics is null)
                {
                    // No topic registry configured; nothing sensible to do with a subscription queue.
                    continue;
                }

                var topicName = name[..subsIdx];
                var subName = name[(subsIdx + EntityNames.SubscriptionsSegment.Length)..];
                // Guard against nested segments we don't model (defensive; addresses are flat).
                if (subName.Contains('/', StringComparison.Ordinal)) continue;

                try
                {
                    if (await _topics.GetTopicAsync(topicName, cancellationToken).ConfigureAwait(false) is null)
                    {
                        await _topics.CreateTopicAsync(new TopicDescriptor { Name = topicName }, cancellationToken).ConfigureAwait(false);
                    }

                    if (await _topics.GetSubscriptionAsync(topicName, subName, cancellationToken).ConfigureAwait(false) is null)
                    {
                        // Creates the backing-queue descriptor (messages already in the store stay put)
                        // and a $Default TrueFilter rule. Custom rules are lost - see the class remarks.
                        await _topics.CreateSubscriptionAsync(
                            new SubscriptionDescriptor { TopicName = topicName, Name = subName }, cancellationToken).ConfigureAwait(false);
                        rehydratedSubscriptions++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to rehydrate subscription '{Topic}/{Sub}' from persistent store.", topicName, subName);
                }
                continue;
            }

            // Plain queue.
            if (knownQueues.Contains(name)) continue;
            await _queues.CreateAsync(new QueueDescriptor { Name = name }, cancellationToken).ConfigureAwait(false);
            rehydratedQueues++;
        }

        if (rehydratedQueues > 0 || rehydratedSubscriptions > 0)
        {
            _logger.LogInformation(
                "Rehydrated {Queues} queue(s) and {Subs} subscription(s) from persistent store with default descriptors.",
                rehydratedQueues, rehydratedSubscriptions);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
