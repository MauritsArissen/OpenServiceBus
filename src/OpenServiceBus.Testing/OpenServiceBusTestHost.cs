using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenServiceBus.Amqp.DeadLettering;
using OpenServiceBus.Amqp.Diagnostics;
using OpenServiceBus.Amqp.Hosting;
using OpenServiceBus.Amqp.Lifecycle;
using OpenServiceBus.Amqp.Topics;
using OpenServiceBus.Amqp.WebSockets;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.InMemoryStorage;
using OpenServiceBus.InMemoryStorage.Lifecycle;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;
using OpenServiceBus.InMemoryStorage.Transactions;
using OpenServiceBus.Management.Atom;
using OpenServiceBus.Management.Atom.Hosting;
using OpenServiceBus.Management.Atom.Protocol;

namespace OpenServiceBus.Testing;

/// <summary>
/// Embeddable, zero-dependency Service Bus broker for use inside unit/integration test fixtures.
/// One instance binds an in-memory broker to a free loopback port and exposes a connection string
/// the Azure SDK can use unmodified. Dispose to release the port.
/// </summary>
/// <example>
/// <code>
/// await using var host = await OpenServiceBusTestHost.StartAsync();
/// await host.CreateQueueAsync("orders");
/// await using var client = new ServiceBusClient(host.ConnectionString);
/// // send / receive as usual
/// </code>
/// </example>
public sealed class OpenServiceBusTestHost : IAsyncDisposable
{
    private readonly AmqpListenerHost _listener;
    private readonly TtlExpirationService _ttlSweeper;
    private readonly ScheduledMessageActivator _scheduledActivator;
    private readonly IdleEntityReaper _idleReaper;
    private readonly WebSocketBridgeService? _wsBridge;
    private readonly ProtocolFrontDoor? _frontDoor;
    private readonly AtomHttpServer? _atomServer;
    private bool _disposed;

    private OpenServiceBusTestHost(
        AmqpListenerHost listener,
        TtlExpirationService ttlSweeper,
        ScheduledMessageActivator scheduledActivator,
        IdleEntityReaper idleReaper,
        WebSocketBridgeService? wsBridge,
        ProtocolFrontDoor? frontDoor,
        AtomHttpServer? atomServer,
        IQueueRegistry queues,
        ITopicRegistry topics,
        IMessageStore store,
        TimeProvider timeProvider,
        int port,
        int? webSocketPort,
        string connectionString,
        string? webSocketConnectionString)
    {
        _listener = listener;
        _ttlSweeper = ttlSweeper;
        _scheduledActivator = scheduledActivator;
        _idleReaper = idleReaper;
        _wsBridge = wsBridge;
        _frontDoor = frontDoor;
        _atomServer = atomServer;
        Queues = queues;
        Topics = topics;
        Store = store;
        TimeProvider = timeProvider;
        Port = port;
        WebSocketPort = webSocketPort;
        ConnectionString = connectionString;
        WebSocketConnectionString = webSocketConnectionString;
    }

    /// <summary>Port the WebSocket bridge is listening on, or null when the bridge isn't enabled.</summary>
    public int? WebSocketPort { get; }

    /// <summary>
    /// Connection string that targets the WebSocket bridge instead of the raw AMQP port.
    /// Pair with <c>ServiceBusClientOptions { TransportType = AmqpWebSockets }</c>. Null when
    /// the bridge isn't enabled.
    /// </summary>
    public string? WebSocketConnectionString { get; }

    /// <summary>Service Bus SDK connection string with <c>UseDevelopmentEmulator=true</c>.</summary>
    public string ConnectionString { get; }

    /// <summary>Raw AMQP URI (<c>amqp://host:port</c>) for AMQPNetLite or low-level clients.</summary>
    public string AmqpUri => $"amqp://127.0.0.1:{Port}";

    /// <summary>Port the broker is listening on.</summary>
    public int Port { get; }

    /// <summary>Queue registry - use to create/list/delete queues from inside tests.</summary>
    public IQueueRegistry Queues { get; }

    /// <summary>Topic registry - use to create/list/delete topics, subscriptions, and rules.</summary>
    public ITopicRegistry Topics { get; }

    /// <summary>In-memory message store - exposed for direct test inspection.</summary>
    public IMessageStore Store { get; }

    /// <summary><see cref="System.TimeProvider"/> the broker is driven by.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Start a broker on a free port (or the port specified in options) and return a ready-to-use host.</summary>
    public static async Task<OpenServiceBusTestHost> StartAsync(Action<OpenServiceBusTestHostOptions>? configure = null)
    {
        var opts = new OpenServiceBusTestHostOptions();
        configure?.Invoke(opts);

        var port = opts.Port ?? GetFreePort();

        // With ATOM management enabled the public port is owned by the protocol front door;
        // the AMQP listener moves to an internal loopback port the front door pipes into.
        var amqpPort = opts.EnableAtomManagement ? GetFreePort() : port;

        var listenerOptions = new AmqpListenerOptions
        {
            Host = opts.EnableAtomManagement ? "127.0.0.1" : opts.Host,
            Port = amqpPort,
            ContainerId = opts.ContainerId,
            IdleTimeoutMs = opts.IdleTimeoutMs,
            MaxMessageSize = opts.MaxMessageSize,
            EnableFrameTracing = opts.EnableFrameTracing,
            RequireSasAuth = opts.RequireSasAuth,
        };
        if (opts.RequireSasAuth)
        {
            listenerOptions.SasKeys[opts.SasKeyName] = opts.SasKey;
            foreach (var (name, key) in opts.AdditionalSasKeys)
            {
                listenerOptions.SasKeys[name] = key;
            }
        }

        // Callers can swap in a different backing store (e.g. SQLite) via opts.StoreFactory.
        // The rest of the stack (registries, router, transactions, listener) is identical and
        // talks to whatever the factory hands back through the IMessageStore interface.
        IMessageStore storeAsIface = opts.StoreFactory is not null
            ? opts.StoreFactory(opts.TimeProvider)
            : new InMemoryMessageStore(opts.TimeProvider);
        var queues = new QueueManager(storeAsIface);
        var topics = new TopicManager(queues, storeAsIface);
        var activity = new EntityActivityTracker(opts.TimeProvider);
        var router = new MessageRouter(queues, storeAsIface, NullLogger<MessageRouter>.Instance, topics,
            new AmqpRuleActionApplier(opts.TimeProvider), activity, new AmqpDeadLetterAnnotator());
        var transactions = new TransactionManager(NullLogger<TransactionManager>.Instance);

        var listener = new AmqpListenerHost(
            Options.Create(listenerOptions),
            queues,
            storeAsIface,
            router,
            transactions,
            opts.TimeProvider,
            NullLoggerFactory.Instance,
            topics,
            activity);

        var ttlSweeper = new TtlExpirationService(
            storeAsIface,
            queues,
            router,
            opts.TimeProvider,
            NullLogger<TtlExpirationService>.Instance);

        var scheduledActivator = new ScheduledMessageActivator(
            storeAsIface,
            queues,
            opts.TimeProvider,
            NullLogger<ScheduledMessageActivator>.Instance);

        var idleReaper = new IdleEntityReaper(
            queues,
            activity,
            opts.TimeProvider,
            NullLogger<IdleEntityReaper>.Instance,
            topics);

        // Register the observable gauges for queue depth. No-op when no MeterListener
        // is attached, so this is essentially free for tests that don't care about telemetry.
        var diagnostics = new DiagnosticsHostedService(storeAsIface, queues);

        // Auto-selected ports are picked with a probe socket that is closed again before the
        // real bind happens, so another process (or a parallel test collection) can grab the
        // port in between. Every bind below retries on a fresh port when that race is lost;
        // a caller-fixed opts.Port still fails loudly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await listener.StartAsync(CancellationToken.None);
                break;
            }
            catch (SocketException) when (opts.Port is null && attempt < 4)
            {
                amqpPort = GetFreePort();
                listenerOptions.Port = amqpPort;
                if (!opts.EnableAtomManagement)
                {
                    port = amqpPort;
                }
                listener = new AmqpListenerHost(
                    Options.Create(listenerOptions),
                    queues,
                    storeAsIface,
                    router,
                    transactions,
                    opts.TimeProvider,
                    NullLoggerFactory.Instance,
                    topics,
                    activity);
            }
        }
        await ttlSweeper.StartAsync(CancellationToken.None);
        await scheduledActivator.StartAsync(CancellationToken.None);
        await idleReaper.StartAsync(CancellationToken.None);
        await diagnostics.StartAsync(CancellationToken.None);

        // The ATOM management surface: the SDK's admin client derives its HTTP endpoint from
        // the same host:port as AMQP, so the public port must speak both protocols. The front
        // door sniffs each connection and pipes it to the AMQP listener or the ATOM server.
        AtomHttpServer? atomServer = null;
        ProtocolFrontDoor? frontDoor = null;
        if (opts.EnableAtomManagement)
        {
            var atomOptions = new AtomManagementOptions();
            if (opts.RequireSasAuth)
            {
                var keys = new Dictionary<string, string>(listenerOptions.SasKeys, StringComparer.Ordinal);
                atomOptions.AuthorizeRequest = header =>
                    SasTokenValidator.Validate(header, keys, opts.TimeProvider.GetUtcNow()).IsValid;
            }
            var handler = new AtomManagementHandler(queues, topics, storeAsIface, opts.TimeProvider, atomOptions);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    atomServer = new AtomHttpServer(handler, GetFreePort(), atomOptions);
                    atomServer.Start();
                    break;
                }
                catch (Exception ex) when (ex is SocketException or HttpListenerException && attempt < 4)
                {
                }
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    frontDoor = new ProtocolFrontDoor(opts.Host, port, amqpPort, atomServer!.Port);
                    frontDoor.Start();
                    break;
                }
                catch (SocketException) when (opts.Port is null && attempt < 4)
                {
                    port = GetFreePort();
                }
            }
        }

        var connectionString =
            $"Endpoint=sb://{opts.Host}:{port};SharedAccessKeyName={opts.SasKeyName};SharedAccessKey={opts.SasKey};UseDevelopmentEmulator=true";

        // Optionally start the AMQP-over-WebSocket bridge on a free port pointing at
        // the listener we just started. The SDK connects to the bridge port instead of the
        // raw AMQP port when TransportType=AmqpWebSockets.
        WebSocketBridgeService? wsBridge = null;
        int? wsPort = null;
        string? wsConnectionString = null;
        if (opts.EnableWebSocketBridge)
        {
            for (var attempt = 0; ; attempt++)
            {
                wsPort = GetFreePort();
                // HttpListener can't bind to "+" on macOS without root, but loopback is fine and
                // matches the test's use case (client + bridge are in the same process).
                var wsOptions = new WebSocketBridgeOptions
                {
                    Enabled = true,
                    Host = opts.Host,
                    Port = wsPort.Value,
                    UpstreamHost = "127.0.0.1",
                    UpstreamPort = amqpPort,
                };
                wsBridge = new WebSocketBridgeService(
                    Options.Create(wsOptions),
                    Options.Create(listenerOptions),
                    NullLogger<WebSocketBridgeService>.Instance);
                try
                {
                    await wsBridge.StartAsync(CancellationToken.None);
                    break;
                }
                catch (Exception ex) when (ex is SocketException or HttpListenerException && attempt < 4)
                {
                }
            }
            wsConnectionString =
                $"Endpoint=sb://{opts.Host}:{wsPort};SharedAccessKeyName={opts.SasKeyName};SharedAccessKey={opts.SasKey};UseDevelopmentEmulator=true";
        }

        return new OpenServiceBusTestHost(
            listener,
            ttlSweeper,
            scheduledActivator,
            idleReaper,
            wsBridge,
            frontDoor,
            atomServer,
            queues,
            topics,
            storeAsIface,
            opts.TimeProvider,
            port,
            wsPort,
            connectionString,
            wsConnectionString);
    }

    /// <summary>Create a queue with default settings. Returns the resulting descriptor.</summary>
    public Task<QueueDescriptor> CreateQueueAsync(string name) =>
        Queues.CreateAsync(new QueueDescriptor { Name = name });

    /// <summary>Create a queue from a pre-built descriptor. Returns the resulting descriptor.</summary>
    public Task<QueueDescriptor> CreateQueueAsync(QueueDescriptor descriptor) =>
        Queues.CreateAsync(descriptor);

    /// <summary>Create a topic with default settings. Returns the resulting descriptor.</summary>
    public Task<TopicDescriptor> CreateTopicAsync(string name) =>
        Topics.CreateTopicAsync(new TopicDescriptor { Name = name });

    /// <summary>Create a topic from a pre-built descriptor. Returns the resulting descriptor.</summary>
    public Task<TopicDescriptor> CreateTopicAsync(TopicDescriptor descriptor) =>
        Topics.CreateTopicAsync(descriptor);

    /// <summary>
    /// Purge every message from every queue and subscription on this broker (including
    /// dead-letter queues) while keeping the topology and live clients intact. Returns
    /// the number of messages removed. See docs/Purge.md.
    /// </summary>
    public async Task<long> PurgeAllAsync(CancellationToken cancellationToken = default) =>
        (await new EntityPurger(Queues, Store, Topics).PurgeAllAsync(cancellationToken)).Purged;

    /// <summary>Purge a queue and its dead-letter queue. Returns the number of messages
    /// removed, or null when the queue does not exist.</summary>
    public Task<long?> PurgeQueueAsync(string name, CancellationToken cancellationToken = default) =>
        new EntityPurger(Queues, Store, Topics).PurgeQueueAsync(name, deadLetterOnly: false, cancellationToken);

    /// <summary>Purge every subscription of a topic (backing queues plus their dead-letter
    /// queues). Returns the number of messages removed, or null when the topic does not exist.</summary>
    public Task<long?> PurgeTopicAsync(string name, CancellationToken cancellationToken = default) =>
        new EntityPurger(Queues, Store, Topics).PurgeTopicAsync(name, cancellationToken);

    /// <summary>Purge a subscription's backing queue and its dead-letter queue. Returns the
    /// number of messages removed, or null when the subscription does not exist.</summary>
    public Task<long?> PurgeSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
        new EntityPurger(Queues, Store, Topics).PurgeSubscriptionAsync(topicName, subscriptionName, deadLetterOnly: false, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_frontDoor is not null)
        {
            await _frontDoor.DisposeAsync();
        }
        if (_atomServer is not null)
        {
            await _atomServer.DisposeAsync();
        }
        if (_wsBridge is not null)
        {
            await _wsBridge.DisposeAsync();
        }
        await _scheduledActivator.StopAsync(CancellationToken.None);
        _scheduledActivator.Dispose();
        await _idleReaper.StopAsync(CancellationToken.None);
        _idleReaper.Dispose();
        await _ttlSweeper.StopAsync(CancellationToken.None);
        _ttlSweeper.Dispose();
        await _listener.StopAsync(CancellationToken.None);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
