using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Serialization of <see cref="RuleDescriptor"/> snapshots for persistent stores, so a
/// subscription's filters and actions survive a broker restart instead of collapsing back to
/// a single <c>$Default</c> match-all rule.
///
/// <see cref="RuleFilter"/> is a polymorphic type holding a parsed expression tree, so the
/// snapshot uses an explicit JSON shape rather than reflection: a discriminated filter node,
/// plus typed parameter/property values (SQL filter parameters and correlation properties are
/// <c>object?</c> and must come back as the same CLR type they went in as, exactly like the
/// ATOM codec's <c>KeyValueOfstringanyType</c> round-trip).
/// </summary>
public static class RuleDescriptorJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(RuleDescriptor rule) =>
        JsonSerializer.Serialize(ToSnapshot(rule), Options);

    /// <summary>
    /// Rebuild a rule from its snapshot. Returns null when the JSON is malformed or carries a
    /// filter/action expression that no longer parses - a corrupt row must never take the
    /// broker down at startup; the subscription simply comes back without that rule.
    /// </summary>
    public static RuleDescriptor? Deserialize(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<RuleSnapshot>(json, Options);
            return snapshot is null ? null : FromSnapshot(snapshot);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static RuleSnapshot ToSnapshot(RuleDescriptor rule) => new()
    {
        TopicName = rule.TopicName,
        SubscriptionName = rule.SubscriptionName,
        Name = rule.Name,
        Filter = ToSnapshot(rule.Filter),
        Action = rule.Action is null
            ? null
            : new ActionSnapshot
            {
                Expression = rule.Action.Expression,
                Parameters = ToSnapshot(rule.Action.Parameters),
            },
    };

    private static FilterSnapshot ToSnapshot(RuleFilter filter) => filter switch
    {
        FalseFilter => new FilterSnapshot { Type = FilterKind.False },
        SqlFilter sql => new FilterSnapshot
        {
            Type = FilterKind.Sql,
            Expression = sql.Expression,
            Parameters = ToSnapshot(sql.Parameters),
        },
        CorrelationFilter c => new FilterSnapshot
        {
            Type = FilterKind.Correlation,
            MessageId = c.MessageId,
            CorrelationId = c.CorrelationId,
            Subject = c.Subject,
            To = c.To,
            ReplyTo = c.ReplyTo,
            ReplyToSessionId = c.ReplyToSessionId,
            SessionId = c.SessionId,
            ContentType = c.ContentType,
            Properties = ToSnapshot(c.Properties),
        },
        // TrueFilter and any filter shape a future version adds fall back to match-all, which
        // is the same widening the ATOM codec applies to an unknown filter.
        _ => new FilterSnapshot { Type = FilterKind.True },
    };

    private static RuleDescriptor? FromSnapshot(RuleSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.TopicName)
            || string.IsNullOrEmpty(snapshot.SubscriptionName)
            || string.IsNullOrEmpty(snapshot.Name))
        {
            return null;
        }

        if (FromSnapshot(snapshot.Filter) is not { } filter) return null;

        return new RuleDescriptor
        {
            TopicName = snapshot.TopicName,
            SubscriptionName = snapshot.SubscriptionName,
            Name = snapshot.Name,
            Filter = filter,
            Action = snapshot.Action is { Expression: { Length: > 0 } expression }
                ? new SqlRuleAction(expression, FromSnapshot(snapshot.Action.Parameters))
                : null,
        };
    }

    /// <summary>
    /// Null for a filter shape this version cannot rebuild, which drops the whole rule. The
    /// tempting fallback - treat it as a TrueFilter - is the one thing that must not happen:
    /// it would quietly turn an unreadable filter into match-all.
    /// </summary>
    private static RuleFilter? FromSnapshot(FilterSnapshot? snapshot)
    {
        // No filter node at all is a match-all rule, same reading the ATOM codec gives it.
        if (snapshot is null) return TrueFilter.Instance;

        switch (snapshot.Type?.ToLowerInvariant())
        {
            case FilterKind.True:
                return TrueFilter.Instance;
            case FilterKind.False:
                return FalseFilter.Instance;
            case FilterKind.Sql:
                if (string.IsNullOrWhiteSpace(snapshot.Expression)) return null;
                return new SqlFilter(snapshot.Expression, FromSnapshot(snapshot.Parameters));
            case FilterKind.Correlation:
                return new CorrelationFilter
                {
                    MessageId = snapshot.MessageId,
                    CorrelationId = snapshot.CorrelationId,
                    Subject = snapshot.Subject,
                    To = snapshot.To,
                    ReplyTo = snapshot.ReplyTo,
                    ReplyToSessionId = snapshot.ReplyToSessionId,
                    SessionId = snapshot.SessionId,
                    ContentType = snapshot.ContentType,
                    Properties = FromSnapshot(snapshot.Properties) is { Count: > 0 } properties
                        ? new Dictionary<string, object?>(properties, StringComparer.Ordinal)
                        : new Dictionary<string, object?>(StringComparer.Ordinal),
                };
            default:
                return null;
        }
    }

    private static Dictionary<string, TypedValue>? ToSnapshot(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var result = new Dictionary<string, TypedValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = ToTypedValue(value);
        }
        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, object?>? FromSnapshot(Dictionary<string, TypedValue>? snapshot)
    {
        if (snapshot is null || snapshot.Count == 0) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, typed) in snapshot)
        {
            result[key] = FromTypedValue(typed);
        }
        return result;
    }

    private static TypedValue ToTypedValue(object? value) => value switch
    {
        null => new TypedValue { Type = ValueKind.Null },
        string s => new TypedValue { Type = ValueKind.String, Value = s },
        bool b => new TypedValue { Type = ValueKind.Bool, Value = b ? "true" : "false" },
        int i => new TypedValue { Type = ValueKind.Int, Value = i.ToString(CultureInfo.InvariantCulture) },
        long l => new TypedValue { Type = ValueKind.Long, Value = l.ToString(CultureInfo.InvariantCulture) },
        double d => new TypedValue { Type = ValueKind.Double, Value = d.ToString("R", CultureInfo.InvariantCulture) },
        DateTimeOffset dto => new TypedValue { Type = ValueKind.DateTime, Value = dto.ToString("O", CultureInfo.InvariantCulture) },
        DateTime dt => new TypedValue
        {
            Type = ValueKind.DateTime,
            Value = new DateTimeOffset(dt.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture),
        },
        _ => new TypedValue
        {
            Type = ValueKind.String,
            Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        },
    };

    private static object? FromTypedValue(TypedValue typed)
    {
        var text = typed.Value ?? string.Empty;
        return typed.Type switch
        {
            ValueKind.Null => null,
            ValueKind.Bool => bool.TryParse(text, out var b) ? b : null,
            ValueKind.Int => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
            ValueKind.Long => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
            ValueKind.Double => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            ValueKind.DateTime => DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) ? dto : null,
            _ => text,
        };
    }

    private static class FilterKind
    {
        public const string True = "true";
        public const string False = "false";
        public const string Sql = "sql";
        public const string Correlation = "correlation";
    }

    private static class ValueKind
    {
        public const string Null = "null";
        public const string String = "string";
        public const string Bool = "bool";
        public const string Int = "int";
        public const string Long = "long";
        public const string Double = "double";
        public const string DateTime = "dateTime";
    }

    private sealed class RuleSnapshot
    {
        public string? TopicName { get; set; }
        public string? SubscriptionName { get; set; }
        public string? Name { get; set; }
        public FilterSnapshot? Filter { get; set; }
        public ActionSnapshot? Action { get; set; }
    }

    private sealed class FilterSnapshot
    {
        public string Type { get; set; } = FilterKind.True;
        public string? Expression { get; set; }
        public Dictionary<string, TypedValue>? Parameters { get; set; }
        public string? MessageId { get; set; }
        public string? CorrelationId { get; set; }
        public string? Subject { get; set; }
        public string? To { get; set; }
        public string? ReplyTo { get; set; }
        public string? ReplyToSessionId { get; set; }
        public string? SessionId { get; set; }
        public string? ContentType { get; set; }
        public Dictionary<string, TypedValue>? Properties { get; set; }
    }

    private sealed class ActionSnapshot
    {
        public string? Expression { get; set; }
        public Dictionary<string, TypedValue>? Parameters { get; set; }
    }

    private sealed class TypedValue
    {
        public string Type { get; set; } = ValueKind.String;
        public string? Value { get; set; }
    }
}
