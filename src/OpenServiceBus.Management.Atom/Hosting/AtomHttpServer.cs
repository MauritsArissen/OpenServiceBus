using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Management.Atom.Protocol;

namespace OpenServiceBus.Management.Atom.Hosting;

/// <summary>
/// Minimal loopback HTTP host for <see cref="AtomManagementHandler"/>, built on
/// <see cref="HttpListener"/> so it works anywhere the BCL does - no ASP.NET dependency,
/// which keeps the embeddable Testing package light. The <see cref="ProtocolFrontDoor"/>
/// forwards HTTP traffic that arrives on the AMQP port here.
/// </summary>
public sealed class AtomHttpServer : IAsyncDisposable
{
    private readonly AtomManagementHandler _handler;
    private readonly int _maxBodyBytes;
    private readonly ILogger _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public AtomHttpServer(AtomManagementHandler handler, int port, AtomManagementOptions? options = null, ILogger<AtomHttpServer>? logger = null)
    {
        _handler = handler;
        _maxBodyBytes = (options ?? new AtomManagementOptions()).MaxRequestBodyBytes;
        _logger = logger ?? NullLogger<AtomHttpServer>.Instance;
        Port = port;
        // Weak wildcard: clients reach the front door under any name (localhost, 127.0.0.1,
        // a docker-compose service name, …) and HttpListener matches prefixes against the
        // forwarded Host header - a fixed host prefix would 404 every other name.
        _listener.Prefixes.Add($"http://*:{port}/");
    }

    /// <summary>Loopback port the server listens on.</summary>
    public int Port { get; }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break; // Listener stopped.
            }
            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;

            string body = string.Empty;
            if (request.HasEntityBody)
            {
                if (request.ContentLength64 > _maxBodyBytes)
                {
                    await WriteResponseAsync(context, new AtomHttpResponse
                    {
                        StatusCode = 413,
                        Body = AtomXml.ErrorBody(413, "Request body too large."),
                    }).ConfigureAwait(false);
                    return;
                }
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                body = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
            }

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in request.QueryString.AllKeys)
            {
                if (key is not null)
                {
                    query[key] = request.QueryString[key] ?? string.Empty;
                }
            }

            var response = await _handler.HandleAsync(new AtomHttpRequest
            {
                Method = request.HttpMethod.ToUpperInvariant(),
                Path = Uri.UnescapeDataString(request.Url?.AbsolutePath ?? "/"),
                Query = query,
                HostHeader = request.Headers["Host"] ?? $"127.0.0.1:{Port}",
                Authorization = request.Headers["Authorization"],
                IfMatch = request.Headers["If-Match"],
                Body = body,
            }, _cts.Token).ConfigureAwait(false);

            await WriteResponseAsync(context, response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "ATOM HTTP request aborted");
            try { context.Response.Abort(); } catch { /* connection already gone */ }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, AtomHttpResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        var bytes = Encoding.UTF8.GetBytes(response.Body);
        if (bytes.Length > 0)
        {
            context.Response.ContentType = response.ContentType;
        }
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* loop exit races the close */ }
        }
        _cts.Dispose();
    }
}
