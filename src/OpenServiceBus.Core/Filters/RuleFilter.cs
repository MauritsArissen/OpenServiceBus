namespace OpenServiceBus.Core.Filters;

/// <summary>
/// Predicate against a message; subscription rules each carry one. Evaluated on the broker's
/// send-to-topic path to decide which subscriptions receive a copy.
/// </summary>
public abstract class RuleFilter
{
    public abstract bool Matches(MessageFilterContext message);
}

/// <summary>Always matches. The default rule on a freshly-created subscription is named <c>$Default</c> with this filter.</summary>
public sealed class TrueFilter : RuleFilter
{
    public static readonly TrueFilter Instance = new();
    public override bool Matches(MessageFilterContext message) => true;
}

/// <summary>Never matches.</summary>
public sealed class FalseFilter : RuleFilter
{
    public static readonly FalseFilter Instance = new();
    public override bool Matches(MessageFilterContext message) => false;
}

/// <summary>
/// Property-equality filter. Each non-null field must equal the corresponding property on the
/// message; non-set fields are wildcards. Cheaper than a SQL filter when you only need equality.
/// </summary>
public sealed class CorrelationFilter : RuleFilter
{
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? To { get; init; }
    public string? ReplyTo { get; init; }
    public string? ReplyToSessionId { get; init; }
    public string? SessionId { get; init; }
    public string? ContentType { get; init; }

    /// <summary>User-property equality constraints. Empty = no user-property constraints.</summary>
    public IDictionary<string, object?> Properties { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public override bool Matches(MessageFilterContext message)
    {
        if (!Match(MessageId, message.MessageId)) return false;
        if (!Match(CorrelationId, message.CorrelationId)) return false;
        if (!Match(Subject, message.Subject)) return false;
        if (!Match(To, message.To)) return false;
        if (!Match(ReplyTo, message.ReplyTo)) return false;
        if (!Match(ReplyToSessionId, message.ReplyToSessionId)) return false;
        if (!Match(SessionId, message.SessionId)) return false;
        if (!Match(ContentType, message.ContentType)) return false;

        foreach (var (key, expected) in Properties)
        {
            if (!message.TryResolve("user", key, out var actual))
                return false;
            if (!PropertyValuesEqual(expected, actual))
                return false;
        }
        return true;
    }

    private static bool Match(string? expected, string? actual) =>
        expected is null || string.Equals(expected, actual, StringComparison.Ordinal);

    private static bool PropertyValuesEqual(object? expected, object? actual)
    {
        if (expected is null || actual is null) return expected is null && actual is null;
        return FilterValueComparer.AreEqual(expected, actual);
    }
}
