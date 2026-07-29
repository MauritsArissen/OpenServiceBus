using Microsoft.Extensions.Options;
using OpenServiceBus.Amqp.Hosting;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.Management.Atom;
using OpenServiceBus.Management.Atom.Hosting;
using OpenServiceBus.Management.Atom.Protocol;

namespace OpenServiceBus.Host;

/// <summary>
/// Where the front door and the AMQP listener meet: Program.cs moves the AMQP listener onto an
/// internal loopback port and records the originally configured public endpoint here.
/// </summary>
public sealed record ManagementGatewaySettings(string PublicHost, int PublicPort, int InternalAmqpPort);

/// <summary>
/// Serves the Service Bus ATOM management API (the protocol behind
/// <c>ServiceBusAdministrationClient</c>) on the broker's public AMQP port. Starts a loopback
/// <see cref="AtomHttpServer"/> plus the <see cref="ProtocolFrontDoor"/> that sniffs each
/// inbound connection and pipes AMQP to the listener and HTTP to the ATOM server - so the one
/// connection string drives both the data plane and the management plane, like Azure.
/// </summary>
public sealed class ManagementGatewayHostedService : IHostedService
{
    private readonly ManagementGatewaySettings _settings;
    private readonly IQueueRegistry _queues;
    private readonly ITopicRegistry _topics;
    private readonly IMessageStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly AmqpListenerOptions _amqpOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ManagementGatewayHostedService> _logger;
    private AtomHttpServer? _atomServer;
    private ProtocolFrontDoor? _frontDoor;

    public ManagementGatewayHostedService(
        ManagementGatewaySettings settings,
        IQueueRegistry queues,
        ITopicRegistry topics,
        IMessageStore store,
        TimeProvider timeProvider,
        IOptions<AmqpListenerOptions> amqpOptions,
        ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _queues = queues;
        _topics = topics;
        _store = store;
        _timeProvider = timeProvider;
        _amqpOptions = amqpOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ManagementGatewayHostedService>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var atomOptions = new AtomManagementOptions();
        if (_amqpOptions.RequireSasAuth)
        {
            // Same trust store and validation as the AMQP $cbs node, applied to the
            // Authorization header the SDK sends on every management request.
            var keys = new Dictionary<string, string>(_amqpOptions.SasKeys, StringComparer.Ordinal);
            atomOptions.AuthorizeRequest = header =>
                SasTokenValidator.Validate(header, keys, _timeProvider.GetUtcNow()).IsValid;
        }

        var handler = new AtomManagementHandler(
            _queues, _topics, _store, _timeProvider, atomOptions,
            _loggerFactory.CreateLogger<AtomManagementHandler>());

        _atomServer = new AtomHttpServer(handler, FreeLoopbackPort(), atomOptions,
            _loggerFactory.CreateLogger<AtomHttpServer>());
        _atomServer.Start();

        _frontDoor = new ProtocolFrontDoor(
            _settings.PublicHost,
            _settings.PublicPort,
            _settings.InternalAmqpPort,
            _atomServer.Port,
            _loggerFactory.CreateLogger<ProtocolFrontDoor>());
        _frontDoor.Start();

        _logger.LogInformation(
            "Management gateway opened on {Host}:{Port} (AMQP upstream 127.0.0.1:{Amqp}, ATOM management upstream 127.0.0.1:{Atom})",
            _settings.PublicHost, _settings.PublicPort, _settings.InternalAmqpPort, _atomServer.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_frontDoor is not null)
        {
            await _frontDoor.DisposeAsync();
        }
        if (_atomServer is not null)
        {
            await _atomServer.DisposeAsync();
        }
    }

    internal static int FreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
