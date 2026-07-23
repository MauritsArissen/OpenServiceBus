using Amqp;
using Amqp.Framing;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Amqp.Hosting;

namespace OpenServiceBus.Amqp.Management;

/// <summary>
/// Handles AMQP Claims-Based Security ($cbs) <c>put-token</c> requests.
///
/// In emulator mode (the OpenServiceBus default), this handler accepts any token
/// and replies with statusCode=202 / statusDescription="Accepted". When
/// <see cref="AmqpListenerOptions.RequireSasAuth"/> is enabled the token is validated
/// against the configured <see cref="AmqpListenerOptions.SasKeys"/> - invalid tokens
/// get 401 Unauthorized and the SDK surfaces an auth failure to the caller.
///
/// Critical wire contract (the Azure SDK / Microsoft.Azure.Amqp is strict and NREs on missing fields).
/// Verified against Microsoft.Azure.Amqp's CbsConstants.cs and AmqpCbsLink.cs:
/// <list type="bullet">
///   <item>Response <c>properties.correlation-id</c> must echo the request's <c>properties.message-id</c> -
///         using the object-typed accessors: proton-j (Java) sends message-ids as AMQP <c>ulong</c>.</item>
///   <item>Response <c>application-properties</c> keys are <b>kebab-case</b>
///         (<c>status-code</c>, <c>status-description</c>) - NOT camelCase.</item>
///   <item><c>status-code</c> is an <c>int32</c> (the SDK does <c>(int)</c> cast directly - wrong int width NREs).</item>
///   <item>The message body is sent as an <c>AmqpValue</c>.</item>
/// </list>
/// </summary>
public sealed class CbsRequestProcessor : IRequestNodeHandler
{
    private readonly AmqpListenerOptions _options;
    private readonly ILogger<CbsRequestProcessor>? _logger;

    public CbsRequestProcessor(AmqpListenerOptions options, ILogger<CbsRequestProcessor>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Link credit advertised to clients on the $cbs request link.</summary>
    public int Credit => 100;

    public Message HandleRequest(Message request)
    {
        if (_options.RequireSasAuth)
        {
            var token = request.Body as string;
            var result = SasTokenValidator.Validate(token, _options.SasKeys, DateTimeOffset.UtcNow);
            if (!result.IsValid)
            {
                _logger?.LogWarning("Rejected $cbs put-token: {Reason}", result.FailureReason);
                return BuildResponse(request, 401, "Unauthorized: " + result.FailureReason);
            }
            _logger?.LogDebug("Accepted $cbs put-token for audience {Audience} (keyName={Key})", result.Audience, result.KeyName);
        }

        return BuildResponse(request, 202, "Accepted");
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

        response.ApplicationProperties["status-code"] = statusCode;
        response.ApplicationProperties["status-description"] = statusDescription;

        return response;
    }
}
