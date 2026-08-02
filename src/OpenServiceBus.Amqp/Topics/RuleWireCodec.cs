using Amqp.Types;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Amqp.Topics;

/// <summary>
/// Encodes/decodes Service Bus rule descriptions to and from the AMQP <c>$management</c> wire
/// format used by <c>com.microsoft:add-rule</c>, <c>com.microsoft:enumerate-rules</c>, and
/// <c>com.microsoft:remove-rule</c>. Format matches what the official
/// <c>Azure.Messaging.ServiceBus</c> <c>ServiceBusRuleManager</c> produces and consumes.
/// </summary>
internal static class RuleWireCodec
{
    // Outer keys in a rule-description map. The Azure SDK emits `sql-filter` (not
    // `sql-rule-filter`); we accept both forms on parse for resilience against legacy
    // Microsoft.Azure.ServiceBus payloads, and emit the modern form on encode.
    public const string SqlFilterKey = "sql-filter";
    public const string CorrelationFilterKey = "correlation-filter";
    public const string TrueFilterKey = "true-filter";
    public const string FalseFilterKey = "false-filter";
    public const string SqlRuleActionKey = "sql-rule-action";

    // Legacy / alternate keys we accept on decode but never emit.
    private static readonly string[] SqlFilterAliases = { SqlFilterKey, "sql-rule-filter" };
    private static readonly string[] CorrelationFilterAliases = { CorrelationFilterKey, "correlation-rule-filter" };
    private static readonly string[] TrueFilterAliases = { TrueFilterKey, "true-rule-filter" };
    private static readonly string[] FalseFilterAliases = { FalseFilterKey, "false-rule-filter" };

    // Filter-payload keys.
    public const string ExpressionKey = "expression";
    public const string CompatibilityLevelKey = "compatibility-level";

    // Correlation-filter payload keys.
    public const string CorrelationIdKey = "correlation-id";
    public const string MessageIdKey = "message-id";
    public const string ToKey = "to";
    public const string ReplyToKey = "reply-to";
    public const string LabelKey = "label";
    public const string SessionIdKey = "session-id";
    public const string ReplyToSessionIdKey = "reply-to-session-id";
    public const string ContentTypeKey = "content-type";
    public const string PropertiesKey = "properties";

    /// <summary>SDK default <c>compatibility-level</c> for SQL filters. Service Bus uses 20.</summary>
    private const int DefaultCompatibilityLevel = 20;

    // AMQP described-type descriptors used by Service Bus for rule management. The Azure SDK's
    // codecs are registered by SYMBOL name primarily, so we emit symbols rather than relying
    // on the ulong code mapping (which differs across SDK versions).
    private static readonly Symbol RuleDescriptionDescriptor = "com.microsoft:rule-description:list";
    private static readonly Symbol EmptyRuleActionDescriptor = "com.microsoft:empty-rule-action:list";
    private static readonly Symbol SqlRuleActionDescriptor = "com.microsoft:sql-rule-action:list";
    private static readonly Symbol SqlFilterDescriptor = "com.microsoft:sql-filter:list";
    private static readonly Symbol TrueFilterDescriptor = "com.microsoft:true-filter:list";
    private static readonly Symbol FalseFilterDescriptor = "com.microsoft:false-filter:list";
    private static readonly Symbol CorrelationFilterDescriptor = "com.microsoft:correlation-filter:list";

    public static RuleFilter DecodeFilter(object descriptionObj) => DecodeRule(descriptionObj).Filter;

    /// <summary>
    /// Decode a <c>rule-description</c> map into the filter plus the optional SQL rule
    /// action (<c>sql-rule-action</c> key, carrying an <c>expression</c>).
    /// </summary>
    public static (RuleFilter Filter, SqlRuleAction? Action) DecodeRule(object descriptionObj)
    {
        if (descriptionObj is DescribedValue dv)
        {
            throw new ArgumentException($"DescribedValue descriptor={dv.Descriptor} ({dv.Descriptor?.GetType().FullName}), value={dv.Value} ({dv.Value?.GetType().FullName})");
        }
        if (descriptionObj is not Map ruleDescription)
        {
            throw new ArgumentException($"rule-description must be a Map (was {descriptionObj?.GetType().FullName}: {descriptionObj}).");
        }

        SqlRuleAction? action = null;
        if (ruleDescription.TryGetValue(SqlRuleActionKey, out var actionObj) && actionObj is Map actionMap)
        {
            var actionExpression = actionMap.TryGetValue(ExpressionKey, out var ae) ? ae as string : null;
            if (!string.IsNullOrWhiteSpace(actionExpression))
            {
                action = new SqlRuleAction(actionExpression);
            }
        }

        return (DecodeFilterFromMap(ruleDescription), action);
    }

    /// <summary>
    /// Decode a <c>rule-description</c> map into a <see cref="RuleFilter"/>. Throws
    /// <see cref="ArgumentException"/> on malformed payloads (the management handler turns
    /// these into 400 BadRequest responses).
    /// </summary>
    private static RuleFilter DecodeFilterFromMap(Map ruleDescription)
    {
        if (TryGetByAlias(ruleDescription, SqlFilterAliases, out var sqlObj) && sqlObj is Map sqlMap)
        {
            var expression = sqlMap.TryGetValue(ExpressionKey, out var e) ? e as string : null;
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("sql-filter requires a non-empty 'expression'.");
            return new SqlFilter(expression);
        }

        if (TryGetByAlias(ruleDescription, CorrelationFilterAliases, out var corrObj) && corrObj is Map corrMap)
        {
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (corrMap.TryGetValue(PropertiesKey, out var p) && p is Map pMap)
            {
                foreach (var key in pMap.Keys)
                {
                    if (key is null) continue;
                    props[key.ToString()!] = pMap[key];
                }
            }
            return new CorrelationFilter
            {
                MessageId = corrMap.TryGetValue(MessageIdKey, out var v1) ? v1 as string : null,
                CorrelationId = corrMap.TryGetValue(CorrelationIdKey, out var v2) ? v2 as string : null,
                Subject = corrMap.TryGetValue(LabelKey, out var v3) ? v3 as string : null,
                To = corrMap.TryGetValue(ToKey, out var v4) ? v4 as string : null,
                ReplyTo = corrMap.TryGetValue(ReplyToKey, out var v5) ? v5 as string : null,
                ReplyToSessionId = corrMap.TryGetValue(ReplyToSessionIdKey, out var v6) ? v6 as string : null,
                SessionId = corrMap.TryGetValue(SessionIdKey, out var v7) ? v7 as string : null,
                ContentType = corrMap.TryGetValue(ContentTypeKey, out var v8) ? v8 as string : null,
                Properties = props,
            };
        }

        if (TryGetByAlias(ruleDescription, TrueFilterAliases, out _))
        {
            return TrueFilter.Instance;
        }
        if (TryGetByAlias(ruleDescription, FalseFilterAliases, out _))
        {
            return FalseFilter.Instance;
        }

        throw new ArgumentException("rule-description must contain one of: sql-filter, correlation-filter, true-filter, false-filter.");
    }

    private static bool TryGetByAlias(Map map, string[] aliases, out object? value)
    {
        foreach (var key in aliases)
        {
            if (map.TryGetValue(key, out value)) return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Encode a <see cref="RuleDescriptor"/> as a <c>rule-description</c> described-list value
    /// for the <c>enumerate-rules</c> response body. The Azure SDK's <c>AmqpRuleManager</c>
    /// requires the response shape to be a <see cref="DescribedValue"/> with the Service Bus
    /// rule-description descriptor code, whose value is a positional list of
    /// <c>[filter, action, name]</c> - each entry itself a described-list value.
    /// </summary>
    public static DescribedValue EncodeRuleDescription(string ruleName, RuleFilter filter, SqlRuleAction? action = null) =>
        new DescribedValue(
            RuleDescriptionDescriptor,
            new List
            {
                EncodeFilter(filter),
                action is null ? EncodeEmptyAction() : EncodeSqlAction(action),
                ruleName,
            });

    private static DescribedValue EncodeFilter(RuleFilter filter) => filter switch
    {
        SqlFilter sql => new DescribedValue(
            SqlFilterDescriptor,
            new List { sql.Expression, DefaultCompatibilityLevel }),
        CorrelationFilter c => new DescribedValue(
            CorrelationFilterDescriptor,
            new List
            {
                c.CorrelationId,
                c.MessageId,
                c.To,
                c.ReplyTo,
                c.Subject, // "label" in wire terms
                c.SessionId,
                c.ReplyToSessionId,
                c.ContentType,
                EncodeCorrelationProps(c.Properties),
            }),
        FalseFilter => new DescribedValue(FalseFilterDescriptor, new List()),
        TrueFilter or _ => new DescribedValue(TrueFilterDescriptor, new List()),
    };

    private static DescribedValue EncodeEmptyAction() =>
        new DescribedValue(EmptyRuleActionDescriptor, new List());

    private static DescribedValue EncodeSqlAction(SqlRuleAction action) =>
        new DescribedValue(SqlRuleActionDescriptor, new List { action.Expression, DefaultCompatibilityLevel });

    private static Map EncodeCorrelationProps(IDictionary<string, object?> properties)
    {
        var map = new Map();
        foreach (var (k, v) in properties) map[k] = v;
        return map;
    }
}
