using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Transactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenServiceBus.Amqp.Management;
using OpenServiceBus.Amqp.Routing;
using OpenServiceBus.Amqp.Transactions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Core.Transactions;
using Trace = Amqp.Trace;
using TraceLevel = Amqp.TraceLevel;

namespace OpenServiceBus.Amqp.Hosting;

public sealed class AmqpListenerHost : IHostedService, IAsyncDisposable
{
    private readonly AmqpListenerOptions _options;
    private readonly ILogger<AmqpListenerHost> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IQueueRegistry _queueRegistry;
    private readonly ITopicRegistry? _topicRegistry;
    private readonly IMessageStore _messageStore;
    private readonly IMessageRouter _router;
    private readonly ITransactionManager _transactions;
    private readonly TimeProvider _timeProvider;
    private ContainerHost? _host;
    private readonly RequestNodeRegistry _requestNodes = new();
    private readonly ResponsePairTable _responsePairs = new();
    private readonly ConnectionAuthRegistry _connectionAuth = new();
    private readonly EntityActivityTracker? _activity;

    public AmqpListenerHost(
        IOptions<AmqpListenerOptions> options,
        IQueueRegistry queueRegistry,
        IMessageStore messageStore,
        IMessageRouter router,
        ITransactionManager transactions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ITopicRegistry? topicRegistry = null,
        EntityActivityTracker? activityTracker = null)
    {
        _activity = activityTracker;
        _options = options.Value;
        _queueRegistry = queueRegistry;
        _topicRegistry = topicRegistry;
        _messageStore = messageStore;
        _router = router;
        _transactions = transactions;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AmqpListenerHost>();
    }

    /// <summary>The address the listener was actually opened on.</summary>
    public Address? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.EnableFrameTracing)
        {
            Trace.TraceLevel = TraceLevel.Frame;
            Trace.TraceListener = (level, fmt, args) =>
                _logger.LogDebug("[amqp] " + fmt, args ?? Array.Empty<object?>());
        }

        var address = new Address(_options.Host, _options.Port, user: null, password: null, path: "/", scheme: "amqp");
        var host = new ContainerHost(address);

        foreach (var listener in host.Listeners)
        {
            ServiceBusSasl.ConfigureListenerMechanisms(listener);

            var handler = new ListenerEventHandler(_options.ContainerId, _options.IdleTimeoutMs, _options.MaxFrameSize);
            listener.HandlerFactory = _ => handler;
        }

        _requestNodes.Register("$cbs", new CbsRequestProcessor(_options, _connectionAuth, _loggerFactory.CreateLogger<CbsRequestProcessor>()));

        host.AddressResolver = (_, attach) => attach.Target is Coordinator ? CoordinatorProcessor.Address : null;
        host.RegisterMessageProcessor(CoordinatorProcessor.Address,
            new CoordinatorProcessor(_transactions, _loggerFactory.CreateLogger<CoordinatorProcessor>()));

        var linkProcessor = new EntityLinkProcessor(_queueRegistry, _messageStore, _router, _transactions, Options.Create(_options), _timeProvider, _loggerFactory, _requestNodes, _responsePairs, _connectionAuth, _topicRegistry, _activity);
        host.RegisterLinkProcessor(linkProcessor);

        host.Open();
        _host = host;
        Address = address;

        // Per-queue $management endpoint registration. Subscribe BEFORE listing so we don't
        // miss any queues created concurrently with startup.
        _queueRegistry.QueueCreated += OnQueueCreated;
        _queueRegistry.QueueUpdated += OnQueueCreated;
        _queueRegistry.QueueDeleted += OnQueueDeleted;
        foreach (var existing in await _queueRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            RegisterManagementEndpoint(existing);
        }

        // Per-subscription $management endpoint registration. Subscription backing
        // queues route here so rule-management ops can resolve through the topic registry.
        // Topics get their own node too, so ScheduleMessageAsync / CancelScheduledMessageAsync
        // against a topic sender have somewhere to land.
        if (_topicRegistry is not null)
        {
            _topicRegistry.TopicCreated += OnTopicCreated;
            _topicRegistry.TopicUpdated += OnTopicCreated;
            _topicRegistry.TopicDeleted += OnTopicDeleted;
            _topicRegistry.SubscriptionCreated += OnSubscriptionCreated;
            _topicRegistry.SubscriptionUpdated += OnSubscriptionCreated;
            _topicRegistry.SubscriptionDeleted += OnSubscriptionDeleted;
            foreach (var topic in await _topicRegistry.ListTopicsAsync(cancellationToken).ConfigureAwait(false))
            {
                RegisterTopicManagementEndpoint(topic);
                foreach (var sub in await _topicRegistry.ListSubscriptionsAsync(topic.Name, cancellationToken).ConfigureAwait(false))
                {
                    RegisterSubscriptionManagementEndpoint(sub);
                }
            }
        }

        _logger.LogInformation(
            "AMQP listener opened on amqp://{Host}:{Port} (containerId={ContainerId}, idleTimeoutMs={Idle}, maxFrameSize={Frame})",
            _options.Host, _options.Port, _options.ContainerId, _options.IdleTimeoutMs, _options.MaxFrameSize);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _queueRegistry.QueueCreated -= OnQueueCreated;
        _queueRegistry.QueueUpdated -= OnQueueCreated;
        _queueRegistry.QueueDeleted -= OnQueueDeleted;
        if (_topicRegistry is not null)
        {
            _topicRegistry.TopicCreated -= OnTopicCreated;
            _topicRegistry.TopicUpdated -= OnTopicCreated;
            _topicRegistry.TopicDeleted -= OnTopicDeleted;
            _topicRegistry.SubscriptionCreated -= OnSubscriptionCreated;
            _topicRegistry.SubscriptionUpdated -= OnSubscriptionCreated;
            _topicRegistry.SubscriptionDeleted -= OnSubscriptionDeleted;
        }

        if (_host is null)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("AMQP listener shutting down");
        try
        {
            _host.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing AMQP listener");
        }
        finally
        {
            _host = null;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    // Also invoked on QueueUpdated: RequestNodeRegistry.Register replaces in place, so
    // re-registering swaps the $management processor's captured descriptor for the new one.
    private void OnQueueCreated(object? sender, QueueDescriptor descriptor) => RegisterManagementEndpoint(descriptor);

    private void OnQueueDeleted(object? sender, QueueDescriptor descriptor)
    {
        if (_host is null) return;

        try
        {
            _requestNodes.Unregister(descriptor.Name + "/$management");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister $management for queue {Queue}", descriptor.Name);
        }
    }

    private void RegisterManagementEndpoint(QueueDescriptor descriptor)
    {
        if (_host is null) return;
        // Subscription-backing queues are registered with subscription context separately
        // - but their DLQ siblings still need a management endpoint of their own so the
        // SDK's PeekMessagesAsync against <topic>/Subscriptions/<sub>/$DeadLetterQueue can land.
        if (IsSubscriptionBackingQueue(descriptor.Name) && !EntityNames.IsDeadLetterQueue(descriptor.Name)) return;

        var processor = new ManagementRequestProcessor(
            descriptor.Name,
            descriptor,
            _messageStore,
            _router,
            _timeProvider,
            _loggerFactory.CreateLogger<ManagementRequestProcessor>(),
            _activity);

        _requestNodes.Register(descriptor.Name + "/$management", processor);
        _logger.LogDebug("Registered $management endpoint for queue {Queue}", descriptor.Name);
    }

    // Also invoked on TopicUpdated: re-registering swaps the processor's captured
    // descriptor snapshot for the new one, same as the queue nodes.
    private void OnTopicCreated(object? sender, TopicDescriptor descriptor) =>
        RegisterTopicManagementEndpoint(descriptor);

    private void OnTopicDeleted(object? sender, TopicDescriptor descriptor)
    {
        if (_host is null) return;
        try
        {
            _requestNodes.Unregister(descriptor.Name + "/$management");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister $management for topic {Topic}", descriptor.Name);
        }
    }

    private void RegisterTopicManagementEndpoint(TopicDescriptor descriptor)
    {
        if (_host is null) return;

        var processor = new ManagementRequestProcessor(
            descriptor,
            _messageStore,
            _router,
            _timeProvider,
            _loggerFactory.CreateLogger<ManagementRequestProcessor>(),
            _activity);

        _requestNodes.Register(descriptor.Name + "/$management", processor);
        _logger.LogDebug("Registered $management endpoint for topic {Topic}", descriptor.Name);
    }

    private void OnSubscriptionCreated(object? sender, SubscriptionDescriptor descriptor) =>
        RegisterSubscriptionManagementEndpoint(descriptor);

    private void OnSubscriptionDeleted(object? sender, SubscriptionDescriptor descriptor)
    {
        if (_host is null) return;
        try
        {
            _requestNodes.Unregister(descriptor.BackingQueueName + "/$management");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister $management for subscription {Sub}", descriptor.BackingQueueName);
        }
    }

    private void RegisterSubscriptionManagementEndpoint(SubscriptionDescriptor descriptor)
    {
        if (_host is null || _topicRegistry is null) return;

        // The processor needs the subscription's QueueDescriptor (for lock duration etc.) -
        // pull it from the queue registry where TopicManager registered it on create.
        var queue = _queueRegistry.GetAsync(descriptor.BackingQueueName).GetAwaiter().GetResult();
        if (queue is null)
        {
            _logger.LogWarning("Backing queue {Name} not found when registering subscription $management - skipping.", descriptor.BackingQueueName);
            return;
        }

        var processor = new ManagementRequestProcessor(
            descriptor.BackingQueueName,
            queue,
            _messageStore,
            _router,
            _timeProvider,
            _loggerFactory.CreateLogger<ManagementRequestProcessor>(),
            _topicRegistry,
            descriptor.TopicName,
            descriptor.Name,
            _activity);

        _requestNodes.Register(descriptor.BackingQueueName + "/$management", processor);
        _logger.LogDebug("Registered $management endpoint for subscription {Sub}", descriptor.BackingQueueName);
    }

    private static bool IsSubscriptionBackingQueue(string queueName) =>
        queueName.Contains("/Subscriptions/", StringComparison.OrdinalIgnoreCase);
}
