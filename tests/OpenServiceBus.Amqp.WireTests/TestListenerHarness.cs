using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Amqp.DependencyInjection;
using OpenServiceBus.Amqp.Hosting;
using OpenServiceBus.Amqp.Lifecycle;
using OpenServiceBus.Amqp.Queues;
using OpenServiceBus.Amqp.Routing;
using OpenServiceBus.InMemoryStorage;
using OpenServiceBus.InMemoryStorage.DependencyInjection;
using OpenServiceBus.InMemoryStorage.Lifecycle;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Transactions;

namespace OpenServiceBus.Amqp.WireTests;

/// <summary>
/// Spins up an <see cref="AmqpListenerHost"/> bound to a random free TCP port on 127.0.0.1
/// with an in-memory broker behind it. Intended for wire-level tests until the full
/// <c>OpenServiceBusTestHost</c> lands.
///
/// Tests needing deterministic time control (e.g. lock expiration) can pass a custom
/// <see cref="TimeProvider"/> (typically <c>FakeTimeProvider</c>) and drive expiration
/// directly via <see cref="IMessageStore.ExpireLocks"/> - the harness does not run the
/// production <c>LockManager</c> sweep loop, so behavior is fully test-controlled.
/// </summary>
internal sealed class TestListenerHarness : IAsyncDisposable
{
    public AmqpListenerHost Host { get; }
    public TtlExpirationService TtlSweeper { get; }
    public ScheduledMessageActivator ScheduledActivator { get; }
    public IQueueRegistry Queues { get; }
    public IMessageStore Store { get; }
    public TimeProvider TimeProvider { get; }
    public int Port { get; }
    public string AmqpUri => $"amqp://127.0.0.1:{Port}";

    private TestListenerHarness(AmqpListenerHost host, TtlExpirationService ttlSweeper, ScheduledMessageActivator scheduledActivator, IQueueRegistry queues, IMessageStore store, TimeProvider timeProvider, int port)
    {
        Host = host;
        TtlSweeper = ttlSweeper;
        ScheduledActivator = scheduledActivator;
        Queues = queues;
        Store = store;
        TimeProvider = timeProvider;
        Port = port;
    }

    public static async Task<TestListenerHarness> StartAsync(
        Action<AmqpListenerOptions>? configure = null,
        TimeProvider? timeProvider = null)
    {
        var port = GetFreePort();
        var options = new AmqpListenerOptions
        {
            Host = "127.0.0.1",
            Port = port,
            ContainerId = "OpenServiceBus.Test",
            IdleTimeoutMs = 30_000,
            MaxFrameSize = 64 * 1024,
        };
        configure?.Invoke(options);

        var tp = timeProvider ?? TimeProvider.System;
        var store = new InMemoryMessageStore(tp);
        IMessageStore storeAsIface = store;
        var queues = new QueueManager(storeAsIface);
        var router = new MessageRouter(queues, storeAsIface, NullLogger<MessageRouter>.Instance,
            deadLetterAnnotator: new OpenServiceBus.Amqp.DeadLettering.AmqpDeadLetterAnnotator());
        var transactions = new TransactionManager(NullLogger<TransactionManager>.Instance);

        var host = new AmqpListenerHost(
            Options.Create(options),
            queues,
            storeAsIface,
            router,
            transactions,
            tp,
            NullLoggerFactory.Instance);

        var ttlSweeper = new TtlExpirationService(
            storeAsIface,
            queues,
            router,
            tp,
            NullLogger<TtlExpirationService>.Instance);

        var scheduledActivator = new ScheduledMessageActivator(
            storeAsIface,
            queues,
            tp,
            NullLogger<ScheduledMessageActivator>.Instance);

        await host.StartAsync(CancellationToken.None);
        await ttlSweeper.StartAsync(CancellationToken.None);
        await scheduledActivator.StartAsync(CancellationToken.None);
        return new TestListenerHarness(host, ttlSweeper, scheduledActivator, queues, storeAsIface, tp, options.Port);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ScheduledActivator.StopAsync(CancellationToken.None);
        ScheduledActivator.Dispose();
        await TtlSweeper.StopAsync(CancellationToken.None);
        TtlSweeper.Dispose();
        await Host.StopAsync(CancellationToken.None);
    }
}
