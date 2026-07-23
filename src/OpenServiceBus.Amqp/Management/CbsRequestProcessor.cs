using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Amqp.Hosting;

namespace OpenServiceBus.Amqp.Management;

/// <summary>
/// Handles AMQP Claims-Based Security ($cbs) <c>put-token</c> requests.
///
/// In emulator mode (the OpenServiceBus default), this processor accepts any token
/// and replies with statusCode=202 / statusDescription="Accepted". When
/// <see cref="AmqpListenerOptions.RequireSasAuth"/> is enabled the token is validated
/// against the configured <see cref="AmqpListenerOptions.SasKeys"/> - invalid tokens
/// get 401 Unauthorized and the SDK surfaces an auth failure to the caller.
///
/// Critical wire contract (the Azure SDK / Microsoft.Azure.Amqp is strict and NREs on missing fields).
/// Verified against Microsoft.Azure.Amqp's CbsConstants.cs and AmqpCbsLink.cs:
/// <list type="bullet">
///   <item>Response <c>properties.correlation-id</c> must echo the request's <c>properties.message-id</c>.</item>
///   <item>Response <c>application-properties</c> keys are <b>kebab-case</b>
///         (<c>status-code</c>, <c>status-description</c>) - NOT camelCase.</item>
///   <item><c>status-code</c> is an <c>int32</c> (the SDK does <c>(int)</c> cast directly - wrong int width NREs).</item>
///   <item>The message body is sent as an <c>AmqpValue</c>.</item>
/// </list>
/// </summary>
public sealed class CbsRequestProcessor : IRequestProcessor
{
    private readonly AmqpListenerOptions _options;
    private readonly ILogger<CbsRequestProcessor>? _logger;

    public CbsRequestProcessor(AmqpListenerOptions options, ILogger<CbsRequestProcessor>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Link credit advertised to clients on the $cbs receiver link.</summary>
    public int Credit => 100;

    public void Process(RequestContext requestContext)
    {
        var request = requestContext.Message;
        Message response;

        if (_options.RequireSasAuth)
        {
            var token = request.Body as string;
            var result = SasTokenValidator.Validate(token, _options.SasKeys, DateTimeOffset.UtcNow);
            if (!result.IsValid)
            {
                _logger?.LogWarning("Rejected $cbs put-token: {Reason}", result.FailureReason);
                response = BuildResponse(request, 401, "Unauthorized: " + result.FailureReason);
                CompleteSafely(requestContext, response);
                return;
            }
            _logger?.LogDebug("Accepted $cbs put-token for audience {Audience} (keyName={Key})", result.Audience, result.KeyName);
        }

        response = BuildResponse(request, 202, "Accepted");
        CompleteSafely(requestContext, response);
    }

    /// <summary>
    /// RequestContext.Complete has two known failure modes with real SDKs:
    /// (1) it re-derives the correlation id via the string-typed <c>Properties.MessageId</c>
    /// getter, which throws InvalidCastException for spec-legal non-string message-ids -
    /// proton-j (the Java SDK) sends CBS message-ids as AMQP ulong; and
    /// (2) it NREs when the client's reply link is gone or was never registered.
    /// The response already carries the correct correlation id (stamped via the object-typed
    /// accessors in BuildResponse), so on (1) we send it directly on the reply link; on (2)
    /// we degrade to a logged drop instead of an unhandled exception in the AMQP pump.
    /// </summary>
    private void CompleteSafely(RequestContext requestContext, Message response)
    {
        try
        {
            requestContext.Complete(response);
            return;
        }
        catch (InvalidCastException)
        {
            // Non-string message-id (proton-j) - fall through to the direct send below.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Could not deliver $cbs response (reply-to '{ReplyTo}') - no matching reply link.",
                requestContext.Message.Properties?.ReplyTo ?? "(null)");
            return;
        }

        try
        {
            requestContext.ResponseLink.SendMessage(response);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Could not deliver $cbs response directly (reply-to '{ReplyTo}').",
                requestContext.Message.Properties?.ReplyTo ?? "(null)");
        }
    }

    private static Message BuildResponse(Message request, int statusCode, string statusDescription)
    {
        var response = new Message
        {
            Properties = new Properties(),
            ApplicationProperties = new ApplicationProperties(),
            BodySection = new AmqpValue { Value = statusDescription },
        };

        var requestMessageId = request.Properties?.GetMessageId();
        if (requestMessageId is not null)
        {
            response.Properties.SetCorrelationId(requestMessageId);
        }

        response.ApplicationProperties["status-code"] = (int)statusCode;
        response.ApplicationProperties["status-description"] = statusDescription;

        return response;
    }
}
