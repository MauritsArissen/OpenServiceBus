using System.Collections.Concurrent;
using System.Threading.Channels;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Amqp.Management;

/// <summary>
/// A request/response node ($cbs, &lt;entity&gt;/$management): takes one request message,
/// returns one response message. Correlation/status stamping is the handler's job.
/// </summary>
public interface IRequestNodeHandler
{
    /// <summary>Link credit advertised to clients on the node's request link.</summary>
    int Credit { get; }

    Message HandleRequest(Message request);
}

/// <summary>
/// Registry of request/response node handlers, keyed by normalized address
/// ("$cbs", "queue/$management", "topic/Subscriptions/sub/$management", ...).
///
/// OpenServiceBus wires these nodes itself instead of using AMQPNetLite's
/// <c>ContainerHost.RegisterRequestProcessor</c>, whose reply routing is keyed by a
/// GLOBAL reply-to address. Real SDKs break that model three ways (GitHub issue #1):
/// rhea attaches reply links with no target address at all, proton-j reuses the fixed
/// address <c>cbs-client-reply-to</c> on every connection (global key collision), and
/// proton-j's ulong message-ids crash the framework's Complete. Azure routes replies by
/// link, not by address - so we pair each node's reply link with its requests via the
/// AMQP session they share, which every SDK stack satisfies.
/// </summary>
public sealed class RequestNodeRegistry
{
    private readonly ConcurrentDictionary<string, IRequestNodeHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string address) =>
        address.Length > 0 && address[0] == '/' ? address[1..] : address;

    /// <summary>True when the address names a request/response node shape, registered or not.</summary>
    public static bool IsRequestResponseAddress(string? address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        var normalized = Normalize(address);
        return normalized.Equals("$cbs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/$management", StringComparison.OrdinalIgnoreCase);
    }

    public void Register(string address, IRequestNodeHandler handler) =>
        _handlers[Normalize(address)] = handler;

    public void Unregister(string address) =>
        _handlers.TryRemove(Normalize(address), out _);

    public bool TryGet(string address, out IRequestNodeHandler handler) =>
        _handlers.TryGetValue(Normalize(address), out handler!);
}

/// <summary>
/// Pairs a node's reply links with its request links through the AMQP session both live
/// on - the pattern all four official SDK stacks use for $cbs and $management.
/// </summary>
public sealed class ResponsePairTable
{
    private readonly ConcurrentDictionary<(object Session, string Address), ResponseChannelSource> _pairs = new();

    public void Register(object session, string address, ResponseChannelSource source) =>
        _pairs[(session, RequestNodeRegistry.Normalize(address))] = source;

    public void Unregister(object session, string address, ResponseChannelSource source)
    {
        var key = (session, RequestNodeRegistry.Normalize(address));
        // Only remove our own registration (a replacement link may have raced us).
        if (_pairs.TryGetValue(key, out var current) && ReferenceEquals(current, source))
        {
            _pairs.TryRemove(key, out _);
        }
        source.Close();
    }

    public bool TryGet(object session, string address, out ResponseChannelSource source) =>
        _pairs.TryGetValue((session, RequestNodeRegistry.Normalize(address)), out source!);
}

/// <summary>
/// Receives request messages on a node's request link, dispatches them to the handler,
/// and forwards the response to the session's paired reply link.
/// </summary>
public sealed class RequestNodeIngressProcessor : IMessageProcessor
{
    private readonly IRequestNodeHandler _handler;
    private readonly ResponsePairTable _pairs;
    private readonly object _session;
    private readonly string _address;
    private readonly ILogger _logger;

    public RequestNodeIngressProcessor(
        IRequestNodeHandler handler,
        ResponsePairTable pairs,
        object session,
        string address,
        ILogger logger)
    {
        _handler = handler;
        _pairs = pairs;
        _session = session;
        _address = address;
        _logger = logger;
    }

    public int Credit => _handler.Credit;

    public void Process(MessageContext messageContext)
    {
        Message response;
        try
        {
            response = _handler.HandleRequest(messageContext.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request node '{Address}' handler failed.", _address);
            messageContext.Complete(new Error(new Symbol(ErrorCode.InternalError)) { Description = ex.Message });
            return;
        }

        messageContext.Complete();
        _ = DeliverResponseAsync(response);
    }

    private async Task DeliverResponseAsync(Message response)
    {
        // The reply link is usually attached before the first request, but nothing in AMQP
        // forbids the request racing ahead - wait briefly before giving up.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (_pairs.TryGet(_session, _address, out var source))
            {
                if (!source.TryEnqueue(response))
                {
                    _logger.LogWarning("Reply link for '{Address}' closed before the response could be sent.", _address);
                }
                return;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "No reply link attached on this session for '{Address}' - response dropped (client sent a request without a paired receiver).",
            _address);
    }
}

/// <summary>
/// The sending side of a request/response node: a push channel the ingress processor
/// enqueues responses into, drained by AMQPNetLite's SourceLinkEndpoint pump.
/// </summary>
public sealed class ResponseChannelSource : IMessageSource
{
    private readonly Channel<Message> _responses = Channel.CreateUnbounded<Message>();

    public bool TryEnqueue(Message response) => _responses.Writer.TryWrite(response);

    public void Close() => _responses.Writer.TryComplete();

    public async Task<ReceiveContext> GetMessageAsync(ListenerLink link)
    {
        // Same drain contract as the queue sources: the SDK drains reply links before
        // closing them, and GetMessageAsync MUST return null so the endpoint can call
        // CompleteDrain - otherwise every management-link close hangs for 60s.
        while (true)
        {
            if (link.IsDraining) return null!;

            using var poll = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                var response = await _responses.Reader.ReadAsync(poll.Token).ConfigureAwait(false);
                return new ReceiveContext(link, response);
            }
            catch (OperationCanceledException)
            {
                // Poll timeout - loop and re-check drain state.
            }
            catch (ChannelClosedException)
            {
                return null!;
            }
        }
    }

    public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext) =>
        dispositionContext.Complete();
}
