namespace OpenServiceBus.Core.Filters;

/// <summary>
/// What a <see cref="RuleFilter"/> sees about a message during evaluation. Mirrors the
/// readable surface of <c>Microsoft.Azure.ServiceBus.Message</c> so SQL filter expressions
/// like <c>sys.MessageId = 'abc' AND user.region = 'eu'</c> can resolve property references
/// without the broker plumbing the AMQP-level message types through every layer.
/// </summary>
public sealed class MessageFilterContext
{
    /// <summary>System properties (the AMQP <c>properties</c> section).</summary>
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? To { get; init; }
    public string? ReplyTo { get; init; }
    public string? ReplyToSessionId { get; init; }
    public string? SessionId { get; init; }
    public string? ContentType { get; init; }

    /// <summary>The enqueued timestamp; available as <c>sys.EnqueuedTimeUtc</c> in SQL filters.</summary>
    public DateTimeOffset EnqueuedTimeUtc { get; init; }

    /// <summary>User-defined application properties (the AMQP <c>application-properties</c> map).</summary>
    public IReadOnlyDictionary<string, object?> ApplicationProperties { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Resolve a property reference using SB rules:
    ///   • <c>sys.X</c>             → system property X
    ///   • <c>user.X</c>            → application property X
    ///   • Unprefixed                → application property X (the Azure SDK default)
    /// </summary>
    public bool TryResolve(string source, string name, out object? value)
    {
        switch (source.ToLowerInvariant())
        {
            case "sys":
                return TryResolveSystem(name, out value);
            case "user":
            case "":
                // "Property names are case-insensitive" per the Service Bus SQL filter
                // docs. Exact match first (free), then a case-insensitive scan.
                if (ApplicationProperties.TryGetValue(name, out value)) return true;
                foreach (var pair in ApplicationProperties)
                {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
                value = null;
                return false;
        }
        value = null;
        return false;
    }

    private bool TryResolveSystem(string name, out object? value)
    {
        value = name.ToLowerInvariant() switch
        {
            "messageid" => MessageId,
            "correlationid" => CorrelationId,
            "subject" or "label" => Subject,
            "to" => To,
            "replyto" => ReplyTo,
            "replytosessionid" => ReplyToSessionId,
            "sessionid" => SessionId,
            "contenttype" => ContentType,
            "enqueuedtimeutc" => EnqueuedTimeUtc,
            _ => null,
        };
        return value is not null
            || name.Equals("MessageId", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CorrelationId", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Subject", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Label", StringComparison.OrdinalIgnoreCase)
            || name.Equals("To", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ReplyTo", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ReplyToSessionId", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SessionId", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ContentType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("EnqueuedTimeUtc", StringComparison.OrdinalIgnoreCase);
    }
}
