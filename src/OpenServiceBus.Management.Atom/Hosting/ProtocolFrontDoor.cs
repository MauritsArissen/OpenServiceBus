using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenServiceBus.Management.Atom.Hosting;

/// <summary>
/// Serves two protocols on the broker's single public port. The Azure SDK derives BOTH its
/// AMQP endpoint (ServiceBusClient) and its HTTP management endpoint
/// (ServiceBusAdministrationClient, with <c>UseDevelopmentEmulator=true</c>) from the one
/// port in the connection string - so that port has to speak both. Every AMQP connection
/// opens with the protocol header <c>"AMQP"</c>; nothing in HTTP/1.1 does. The front door
/// peeks the first bytes of each accepted connection and pipes it verbatim to the matching
/// loopback backend: the AMQP listener or the <see cref="AtomHttpServer"/>.
/// </summary>
public sealed class ProtocolFrontDoor : IAsyncDisposable
{
    private static readonly TimeSpan SniffTimeout = TimeSpan.FromSeconds(30);

    private readonly string _host;
    private readonly int _amqpUpstreamPort;
    private readonly int _httpUpstreamPort;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _liveClients = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public ProtocolFrontDoor(
        string host,
        int port,
        int amqpUpstreamPort,
        int httpUpstreamPort,
        ILogger<ProtocolFrontDoor>? logger = null)
    {
        _host = host;
        Port = port;
        _amqpUpstreamPort = amqpUpstreamPort;
        _httpUpstreamPort = httpUpstreamPort;
        _logger = logger ?? NullLogger<ProtocolFrontDoor>.Instance;
    }

    /// <summary>The public port both protocols share.</summary>
    public int Port { get; }

    public void Start()
    {
        var address = _host is "0.0.0.0" or "+" or "*" ? IPAddress.Any
            : IPAddress.TryParse(_host, out var parsed) ? parsed
            : IPAddress.Loopback;
        _listener = new TcpListener(address, Port);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var upstream = new TcpClient();
        _liveClients[client] = 0;
        _liveClients[upstream] = 0;
        try
        {
            client.NoDelay = true;
            var clientStream = client.GetStream();

            // Sniff up to 4 bytes. AMQP's protocol header is 8 bytes sent immediately and
            // always starts with "AMQP"; an HTTP request line can't. Anything shorter that
            // hits EOF or isn't AMQP goes to the HTTP backend.
            var preamble = new byte[4];
            var read = 0;
            using (var sniffCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                sniffCts.CancelAfter(SniffTimeout);
                while (read < preamble.Length)
                {
                    var n = await clientStream.ReadAsync(preamble.AsMemory(read), sniffCts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
            }
            if (read == 0)
            {
                return; // Connected and hung up (or said nothing for the whole sniff window).
            }

            var isAmqp = read == 4 && preamble is [0x41, 0x4D, 0x51, 0x50]; // "AMQP"
            var upstreamPort = isAmqp ? _amqpUpstreamPort : _httpUpstreamPort;

            await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, _cts.Token).ConfigureAwait(false);
            upstream.NoDelay = true;
            var upstreamStream = upstream.GetStream();
            await upstreamStream.WriteAsync(preamble.AsMemory(0, read), _cts.Token).ConfigureAwait(false);

            await Task.WhenAll(
                PumpAsync(clientStream, upstreamStream, upstream),
                PumpAsync(upstreamStream, clientStream, client)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Normal connection teardown noise - either side vanished mid-pipe.
            _logger.LogTrace(ex, "Front-door connection closed");
        }
        finally
        {
            _liveClients.TryRemove(client, out _);
            _liveClients.TryRemove(upstream, out _);
            client.Dispose();
            upstream.Dispose();
        }
    }

    /// <summary>
    /// Copy until EOF, then propagate the half-close so protocols that shut down one
    /// direction first (AMQP close handshakes, HTTP keep-alive teardown) behave exactly as
    /// they would on a direct connection.
    /// </summary>
    private async Task PumpAsync(NetworkStream source, NetworkStream destination, TcpClient destinationClient)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await source.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, n), _cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try { destinationClient.Client.Shutdown(SocketShutdown.Send); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { /* already closed */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener?.Stop(); } catch { /* already stopped */ }
        foreach (var client in _liveClients.Keys)
        {
            client.Dispose();
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* races the stop */ }
        }
        _cts.Dispose();
    }
}
