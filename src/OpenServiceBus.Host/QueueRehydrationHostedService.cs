using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Host;

/// <summary>
/// After config bootstrap, ensure the in-memory registries have an entry for every entity the
/// backing <see cref="IMessageStore"/> knows about. Only relevant with a persistent store
/// (SQLite): on restart the SQLite file still holds the entity rows but the registries are
/// empty memory.
///
/// The restore itself is <see cref="EntityRehydrator"/>; this wrapper owns the hosting
/// lifetime and the logging. Queues, topics, subscriptions and subscription rules all come
/// back from their persisted snapshots; <c>config.json</c> still runs first and wins for
/// anything it declares.
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
        var rehydrator = new EntityRehydrator(
            _store,
            _queues,
            _topics,
            (context, ex) => _logger.LogError(ex, "Failed to rehydrate {Entity} from persistent store.", context));

        var result = await rehydrator.RunAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsEmpty) return;

        _logger.LogInformation(
            "Rehydrated {Queues} queue(s), {Topics} topic(s), {Subs} subscription(s) and {Rules} rule(s) from persistent store.",
            result.Queues, result.Topics, result.Subscriptions, result.Rules);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
