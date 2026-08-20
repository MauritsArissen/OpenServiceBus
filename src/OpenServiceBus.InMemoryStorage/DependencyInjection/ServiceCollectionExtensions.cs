using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Core.Transactions;
using OpenServiceBus.InMemoryStorage.CannedMessages;
using OpenServiceBus.InMemoryStorage.Lifecycle;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;
using OpenServiceBus.InMemoryStorage.Transactions;

namespace OpenServiceBus.InMemoryStorage.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory storage adapter: <see cref="InMemoryMessageStore"/>, <see cref="QueueManager"/>,
    /// <see cref="TopicManager"/>, a singleton <see cref="TimeProvider"/> (system clock by default;
    /// tests can replace with FakeTimeProvider), and the <see cref="LockManager"/> background sweeper.
    /// </summary>
    public static IServiceCollection AddOpenServiceBusInMemoryStorage(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<EntityActivityTracker>();
        services.TryAddSingleton<InMemoryMessageStore>();
        services.TryAddSingleton<IMessageStore>(sp => sp.GetRequiredService<InMemoryMessageStore>());
        services.TryAddSingleton<QueueManager>();
        services.TryAddSingleton<IQueueRegistry>(sp => sp.GetRequiredService<QueueManager>());
        services.TryAddSingleton<TopicManager>();
        services.TryAddSingleton<ITopicRegistry>(sp => sp.GetRequiredService<TopicManager>());
        services.TryAddSingleton<IMessageRouter, MessageRouter>();
        services.TryAddSingleton<ITransactionManager, TransactionManager>();
        services.AddHostedService<LockManager>();
        services.TryAddSingleton<CannedMessagesManager>();
        services.TryAddSingleton<ICannedMessagesRegistry>(sp => sp.GetRequiredService<CannedMessagesManager>());
        return services;
    }
}
