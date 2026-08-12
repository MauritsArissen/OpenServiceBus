using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Management.Atom.Protocol;

/// <summary>
/// Snapshot of a queue-like entity's message counters, fed into the runtime-property elements
/// (<c>MessageCount</c>, <c>CountDetails</c>) that back the SDK's <c>Get*RuntimePropertiesAsync</c>.
/// </summary>
public readonly record struct EntityRuntimeInfo(
    long ActiveMessageCount,
    long ScheduledMessageCount,
    long DeadLetterMessageCount,
    long SizeInBytes,
    long TransferDeadLetterMessageCount = 0)
{
    public long TotalMessageCount =>
        ActiveMessageCount + ScheduledMessageCount + DeadLetterMessageCount + TransferDeadLetterMessageCount;
}

/// <summary>
/// Serializes broker descriptors to the entity-description XML the official SDKs parse, and
/// parses incoming descriptions back into descriptors. Properties the broker enforces
/// round-trip; the rest are emitted with Azure's defaults so every SDK parser stays happy,
/// and are accepted-but-ignored on the way in (documented in docs/Admin-Client.md).
/// </summary>
public static class EntityDescriptionCodec
{
    private static readonly XNamespace Sb = AtomXml.Sb;

    // ── Queues ──

    public static XElement QueueDescription(QueueDescriptor queue, EntityRuntimeInfo runtime)
    {
        var updated = queue.UpdatedAt ?? queue.CreatedAt;
        return new XElement(Sb + "QueueDescription",
            new XAttribute(XNamespace.Xmlns + "i", AtomXml.Xsi),
            new XElement(Sb + "LockDuration", AtomXml.Duration(queue.LockDuration)),
            new XElement(Sb + "MaxSizeInMegabytes", queue.MaxSizeInMegabytes),
            new XElement(Sb + "RequiresDuplicateDetection", queue.RequiresDuplicateDetection),
            new XElement(Sb + "RequiresSession", queue.RequiresSession),
            new XElement(Sb + "DefaultMessageTimeToLive", AtomXml.DurationOrUnlimited(queue.DefaultMessageTimeToLive)),
            new XElement(Sb + "DeadLetteringOnMessageExpiration", queue.DeadLetteringOnMessageExpiration),
            new XElement(Sb + "DuplicateDetectionHistoryTimeWindow",
                AtomXml.Duration(queue.DuplicateDetectionHistoryTimeWindow ?? TimeSpan.FromMinutes(10))),
            new XElement(Sb + "MaxDeliveryCount", queue.MaxDeliveryCount),
            new XElement(Sb + "EnableBatchedOperations", true),
            new XElement(Sb + "SizeInBytes", runtime.SizeInBytes),
            new XElement(Sb + "MessageCount", runtime.TotalMessageCount),
            new XElement(Sb + "IsAnonymousAccessible", false),
            new XElement(Sb + "AuthorizationRules"),
            new XElement(Sb + "Status", queue.Status.ToString()),
            ForwardElement("ForwardTo", queue.ForwardTo),
            UserMetadataElement(queue.UserMetadata),
            new XElement(Sb + "CreatedAt", AtomXml.Timestamp(queue.CreatedAt)),
            new XElement(Sb + "UpdatedAt", AtomXml.Timestamp(updated)),
            new XElement(Sb + "AccessedAt", AtomXml.Timestamp(updated)),
            new XElement(Sb + "SupportOrdering", true),
            CountDetailsElement(runtime),
            new XElement(Sb + "AutoDeleteOnIdle", AtomXml.DurationOrUnlimited(queue.AutoDeleteOnIdle)),
            new XElement(Sb + "EnablePartitioning", false),
            new XElement(Sb + "EntityAvailabilityStatus", "Available"),
            new XElement(Sb + "EnableExpress", false),
            ForwardElement("ForwardDeadLetteredMessagesTo", queue.ForwardDeadLetteredMessagesTo),
            new XElement(Sb + "MaxMessageSizeInKilobytes", queue.MaxMessageSizeInKilobytes));
    }

    public static QueueDescriptor ParseQueueDescription(string name, XElement description)
    {
        var queue = new QueueDescriptor { Name = name };
        foreach (var element in description.Elements())
        {
            var value = element.Value;
            queue = element.Name.LocalName switch
            {
                "LockDuration" => queue with { LockDuration = AtomXml.ParseDuration(value) },
                "RequiresDuplicateDetection" => queue with { RequiresDuplicateDetection = XmlConvert.ToBoolean(value) },
                "RequiresSession" => queue with { RequiresSession = XmlConvert.ToBoolean(value) },
                "DefaultMessageTimeToLive" => queue with { DefaultMessageTimeToLive = AtomXml.ParseOptionalDuration(value) },
                "DeadLetteringOnMessageExpiration" => queue with { DeadLetteringOnMessageExpiration = XmlConvert.ToBoolean(value) },
                "DuplicateDetectionHistoryTimeWindow" => queue with { DuplicateDetectionHistoryTimeWindow = AtomXml.ParseDuration(value) },
                "MaxDeliveryCount" => queue with { MaxDeliveryCount = XmlConvert.ToInt32(value) },
                "ForwardTo" => queue with { ForwardTo = NormalizeForwardTarget(value) },
                "ForwardDeadLetteredMessagesTo" => queue with { ForwardDeadLetteredMessagesTo = NormalizeForwardTarget(value) },
                "UserMetadata" => queue with { UserMetadata = value },
                "Status" => queue with { Status = EntityStatusExtensions.Parse(value) },
                "AutoDeleteOnIdle" => queue with { AutoDeleteOnIdle = AtomXml.ParseOptionalDuration(value) },
                "MaxSizeInMegabytes" => queue with { MaxSizeInMegabytes = XmlConvert.ToInt64(value) },
                "MaxMessageSizeInKilobytes" => queue with { MaxMessageSizeInKilobytes = XmlConvert.ToInt64(value) },
                _ => queue, // Accepted-but-ignored (MaxSizeInMegabytes, Status, AutoDeleteOnIdle, …).
            };
        }
        return queue;
    }

    // ── Topics ──

    public static XElement TopicDescription(TopicDescriptor topic, int subscriptionCount)
    {
        var updated = topic.UpdatedAt ?? topic.CreatedAt;
        return new XElement(Sb + "TopicDescription",
            new XAttribute(XNamespace.Xmlns + "i", AtomXml.Xsi),
            new XElement(Sb + "DefaultMessageTimeToLive", AtomXml.DurationOrUnlimited(topic.DefaultMessageTimeToLive)),
            new XElement(Sb + "MaxSizeInMegabytes", topic.MaxSizeInMegabytes),
            new XElement(Sb + "RequiresDuplicateDetection", topic.RequiresDuplicateDetection),
            new XElement(Sb + "DuplicateDetectionHistoryTimeWindow",
                AtomXml.Duration(topic.DuplicateDetectionHistoryTimeWindow ?? DuplicateDetection.DefaultWindow)),
            new XElement(Sb + "EnableBatchedOperations", true),
            new XElement(Sb + "SizeInBytes", 0),
            new XElement(Sb + "FilteringMessagesBeforePublishing", false),
            new XElement(Sb + "IsAnonymousAccessible", false),
            new XElement(Sb + "AuthorizationRules"),
            new XElement(Sb + "Status", topic.Status.ToString()),
            UserMetadataElement(topic.UserMetadata),
            new XElement(Sb + "CreatedAt", AtomXml.Timestamp(topic.CreatedAt)),
            new XElement(Sb + "UpdatedAt", AtomXml.Timestamp(updated)),
            new XElement(Sb + "AccessedAt", AtomXml.Timestamp(updated)),
            new XElement(Sb + "SupportOrdering", true),
            CountDetailsElement(default),
            new XElement(Sb + "SubscriptionCount", subscriptionCount),
            new XElement(Sb + "AutoDeleteOnIdle", AtomXml.DurationOrUnlimited(topic.AutoDeleteOnIdle)),
            new XElement(Sb + "EnablePartitioning", false),
            new XElement(Sb + "EntityAvailabilityStatus", "Available"),
            new XElement(Sb + "EnableExpress", false),
            new XElement(Sb + "MaxMessageSizeInKilobytes", topic.MaxMessageSizeInKilobytes));
    }

    public static TopicDescriptor ParseTopicDescription(string name, XElement description)
    {
        var topic = new TopicDescriptor { Name = name };
        foreach (var element in description.Elements())
        {
            var value = element.Value;
            topic = element.Name.LocalName switch
            {
                "DefaultMessageTimeToLive" => topic with { DefaultMessageTimeToLive = AtomXml.ParseOptionalDuration(value) },
                "RequiresDuplicateDetection" => topic with { RequiresDuplicateDetection = XmlConvert.ToBoolean(value) },
                "DuplicateDetectionHistoryTimeWindow" => topic with { DuplicateDetectionHistoryTimeWindow = AtomXml.ParseOptionalDuration(value) },
                "UserMetadata" => topic with { UserMetadata = value },
                "Status" => topic with { Status = EntityStatusExtensions.Parse(value) },
                "AutoDeleteOnIdle" => topic with { AutoDeleteOnIdle = AtomXml.ParseOptionalDuration(value) },
                "MaxSizeInMegabytes" => topic with { MaxSizeInMegabytes = XmlConvert.ToInt64(value) },
                "MaxMessageSizeInKilobytes" => topic with { MaxMessageSizeInKilobytes = XmlConvert.ToInt64(value) },
                _ => topic,
            };
        }
        return topic;
    }

    // ── Subscriptions ──

    public static XElement SubscriptionDescription(SubscriptionDescriptor subscription, EntityRuntimeInfo runtime)
    {
        var updated = subscription.UpdatedAt ?? subscription.CreatedAt;
        return new XElement(Sb + "SubscriptionDescription",
            new XAttribute(XNamespace.Xmlns + "i", AtomXml.Xsi),
            new XElement(Sb + "LockDuration", AtomXml.Duration(subscription.LockDuration)),
            new XElement(Sb + "RequiresSession", subscription.RequiresSession),
            new XElement(Sb + "DefaultMessageTimeToLive", AtomXml.DurationOrUnlimited(subscription.DefaultMessageTimeToLive)),
            new XElement(Sb + "DeadLetteringOnMessageExpiration", subscription.DeadLetteringOnMessageExpiration),
            new XElement(Sb + "DeadLetteringOnFilterEvaluationExceptions", true),
            new XElement(Sb + "MessageCount", runtime.TotalMessageCount),
            new XElement(Sb + "MaxDeliveryCount", subscription.MaxDeliveryCount),
            new XElement(Sb + "EnableBatchedOperations", true),
            new XElement(Sb + "Status", subscription.Status.ToString()),
            ForwardElement("ForwardTo", subscription.ForwardTo),
            UserMetadataElement(subscription.UserMetadata),
            new XElement(Sb + "CreatedAt", AtomXml.Timestamp(subscription.CreatedAt)),
            new XElement(Sb + "UpdatedAt", AtomXml.Timestamp(updated)),
            new XElement(Sb + "AccessedAt", AtomXml.Timestamp(updated)),
            CountDetailsElement(runtime),
            new XElement(Sb + "AutoDeleteOnIdle", AtomXml.DurationOrUnlimited(subscription.AutoDeleteOnIdle)),
            new XElement(Sb + "EntityAvailabilityStatus", "Available"),
            ForwardElement("ForwardDeadLetteredMessagesTo", subscription.ForwardDeadLetteredMessagesTo));
    }

    /// <summary>
    /// Parse a SubscriptionDescription. When the SDK piggybacks the initial rule on create
    /// (<c>CreateSubscriptionAsync(options, ruleOptions)</c>) it lands as an embedded
    /// <c>DefaultRuleDescription</c> element - returned separately so the handler can install it
    /// in place of the implicit $Default TrueFilter rule.
    /// </summary>
    public static (SubscriptionDescriptor Subscription, RuleDescriptor? DefaultRule) ParseSubscriptionDescription(
        string topicName, string subscriptionName, XElement description)
    {
        var subscription = new SubscriptionDescriptor { TopicName = topicName, Name = subscriptionName };
        RuleDescriptor? defaultRule = null;
        foreach (var element in description.Elements())
        {
            if (element.Name.LocalName == "DefaultRuleDescription")
            {
                var (filter, action, ruleName) = ParseRuleBody(element);
                defaultRule = new RuleDescriptor
                {
                    TopicName = topicName,
                    SubscriptionName = subscriptionName,
                    Name = ruleName ?? "$Default",
                    Filter = filter,
                    Action = action,
                };
                continue;
            }

            var value = element.Value;
            subscription = element.Name.LocalName switch
            {
                "LockDuration" => subscription with { LockDuration = AtomXml.ParseDuration(value) },
                "RequiresSession" => subscription with { RequiresSession = XmlConvert.ToBoolean(value) },
                "DefaultMessageTimeToLive" => subscription with { DefaultMessageTimeToLive = AtomXml.ParseOptionalDuration(value) },
                "DeadLetteringOnMessageExpiration" => subscription with { DeadLetteringOnMessageExpiration = XmlConvert.ToBoolean(value) },
                "MaxDeliveryCount" => subscription with { MaxDeliveryCount = XmlConvert.ToInt32(value) },
                "ForwardTo" => subscription with { ForwardTo = NormalizeForwardTarget(value) },
                "ForwardDeadLetteredMessagesTo" => subscription with { ForwardDeadLetteredMessagesTo = NormalizeForwardTarget(value) },
                "UserMetadata" => subscription with { UserMetadata = value },
                "Status" => subscription with { Status = EntityStatusExtensions.Parse(value) },
                "AutoDeleteOnIdle" => subscription with { AutoDeleteOnIdle = AtomXml.ParseOptionalDuration(value) },
                _ => subscription,
            };
        }
        return (subscription, defaultRule);
    }

    // ── Rules ──

    public static XElement RuleDescription(RuleDescriptor rule, DateTimeOffset createdAt)
    {
        var actionElement = rule.Action is null
            ? new XElement(Sb + "Action", new XAttribute(AtomXml.Xsi + "type", "EmptyRuleAction"))
            : new XElement(Sb + "Action",
                new XAttribute(AtomXml.Xsi + "type", "SqlRuleAction"),
                new XElement(Sb + "SqlExpression", rule.Action.Expression),
                new XElement(Sb + "CompatibilityLevel", 20),
                SqlParametersElement(rule.Action.Parameters));
        return new XElement(Sb + "RuleDescription",
            new XAttribute(XNamespace.Xmlns + "i", AtomXml.Xsi),
            FilterElement(rule.Filter),
            actionElement,
            new XElement(Sb + "CreatedAt", AtomXml.Timestamp(createdAt)),
            new XElement(Sb + "Name", rule.Name));
    }

    public static RuleDescriptor ParseRuleDescription(string topicName, string subscriptionName, string ruleName, XElement description)
    {
        var (filter, action, _) = ParseRuleBody(description);
        return new RuleDescriptor
        {
            TopicName = topicName,
            SubscriptionName = subscriptionName,
            Name = ruleName,
            Filter = filter,
            Action = action,
        };
    }

    // ── Namespace ──

    public static XElement NamespaceInfo(string namespaceName, DateTimeOffset createdAt)
    {
        return new XElement(Sb + "NamespaceInfo",
            new XAttribute(XNamespace.Xmlns + "i", AtomXml.Xsi),
            new XElement(Sb + "CreatedTime", AtomXml.Timestamp(createdAt)),
            new XElement(Sb + "MessagingSKU", "Standard"),
            new XElement(Sb + "MessagingUnits", 0),
            new XElement(Sb + "ModifiedTime", AtomXml.Timestamp(createdAt)),
            new XElement(Sb + "Name", namespaceName),
            new XElement(Sb + "NamespaceType", "Messaging"));
    }

    // ── Shared pieces ──

    private static XElement CountDetailsElement(EntityRuntimeInfo runtime)
    {
        var d = AtomXml.CountDetails;
        return new XElement(Sb + "CountDetails",
            new XAttribute(XNamespace.Xmlns + "d2p1", d),
            new XElement(d + "ActiveMessageCount", runtime.ActiveMessageCount),
            new XElement(d + "DeadLetterMessageCount", runtime.DeadLetterMessageCount),
            new XElement(d + "ScheduledMessageCount", runtime.ScheduledMessageCount),
            new XElement(d + "TransferMessageCount", 0),
            new XElement(d + "TransferDeadLetterMessageCount", runtime.TransferDeadLetterMessageCount));
    }

    private static XElement? ForwardElement(string name, string? target) =>
        string.IsNullOrEmpty(target) ? null : new XElement(Sb + name, target);

    private static XElement? UserMetadataElement(string? metadata) =>
        metadata is null ? null : new XElement(Sb + "UserMetadata", metadata);

    /// <summary>
    /// The SDK sends forward targets as absolute endpoint URIs (<c>https://ns/entity</c>);
    /// the broker routes by bare entity path. Strip scheme+authority when present.
    /// </summary>
    internal static string? NormalizeForwardTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            return path.Length == 0 ? null : path;
        }
        return value.Trim('/');
    }

    private static XElement FilterElement(RuleFilter filter)
    {
        var element = new XElement(Sb + "Filter");
        switch (filter)
        {
            case TrueFilter:
                element.SetAttributeValue(AtomXml.Xsi + "type", "TrueFilter");
                element.Add(new XElement(Sb + "SqlExpression", "1=1"),
                    new XElement(Sb + "CompatibilityLevel", 20));
                break;
            case FalseFilter:
                element.SetAttributeValue(AtomXml.Xsi + "type", "FalseFilter");
                element.Add(new XElement(Sb + "SqlExpression", "1=0"),
                    new XElement(Sb + "CompatibilityLevel", 20));
                break;
            case SqlFilter sql:
                element.SetAttributeValue(AtomXml.Xsi + "type", "SqlFilter");
                element.Add(new XElement(Sb + "SqlExpression", sql.Expression),
                    new XElement(Sb + "CompatibilityLevel", 20));
                if (SqlParametersElement(sql.Parameters) is { } filterParameters)
                {
                    element.Add(filterParameters);
                }
                break;
            case CorrelationFilter c:
                element.SetAttributeValue(AtomXml.Xsi + "type", "CorrelationFilter");
                AddIfSet("CorrelationId", c.CorrelationId);
                AddIfSet("MessageId", c.MessageId);
                AddIfSet("To", c.To);
                AddIfSet("ReplyTo", c.ReplyTo);
                AddIfSet("Label", c.Subject);
                AddIfSet("SessionId", c.SessionId);
                AddIfSet("ReplyToSessionId", c.ReplyToSessionId);
                AddIfSet("ContentType", c.ContentType);
                if (c.Properties.Count > 0)
                {
                    var props = new XElement(Sb + "Properties");
                    foreach (var (key, value) in c.Properties)
                    {
                        props.Add(CorrelationProperty(key, value));
                    }
                    element.Add(props);
                }
                break;
            default:
                element.SetAttributeValue(AtomXml.Xsi + "type", "TrueFilter");
                element.Add(new XElement(Sb + "SqlExpression", "1=1"),
                    new XElement(Sb + "CompatibilityLevel", 20));
                break;
        }
        return element;

        void AddIfSet(string name, string? value)
        {
            if (value is not null) element.Add(new XElement(Sb + name, value));
        }
    }

    private static XElement CorrelationProperty(string key, object? value)
    {
        var (xsType, text) = value switch
        {
            null => ("string", string.Empty),
            string s => ("string", s),
            bool b => ("boolean", XmlConvert.ToString(b)),
            int i => ("int", XmlConvert.ToString(i)),
            long l => ("long", XmlConvert.ToString(l)),
            double d => ("double", XmlConvert.ToString(d)),
            DateTimeOffset dto => ("dateTime", AtomXml.Timestamp(dto)),
            DateTime dt => ("dateTime", AtomXml.Timestamp(new DateTimeOffset(dt.ToUniversalTime()))),
            _ => ("string", Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
        var valueElement = new XElement(Sb + "Value",
            new XAttribute(XNamespace.Xmlns + "d6p1", AtomXml.Xs),
            new XAttribute(AtomXml.Xsi + "type", $"d6p1:{xsType}"),
            text);
        return new XElement(Sb + "KeyValueOfstringanyType",
            new XElement(Sb + "Key", key),
            valueElement);
    }

    /// <summary>Parse a Filter (+ optional Action and Name) from a RuleDescription or DefaultRuleDescription body.</summary>
    private static (RuleFilter Filter, SqlRuleAction? Action, string? Name) ParseRuleBody(XElement ruleElement)
    {
        var name = ruleElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
        var filterElement = ruleElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Filter");

        SqlRuleAction? action = null;
        var actionElement = ruleElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Action");
        if (actionElement is not null)
        {
            var typeValue = actionElement.Attribute(AtomXml.Xsi + "type")?.Value ?? "EmptyRuleAction";
            var colon = typeValue.IndexOf(':');
            var actionType = colon >= 0 ? typeValue[(colon + 1)..] : typeValue;
            if (actionType == "SqlRuleAction")
            {
                var expression = actionElement.Elements().FirstOrDefault(e => e.Name.LocalName == "SqlExpression")?.Value;
                if (string.IsNullOrWhiteSpace(expression))
                {
                    throw new FormatException("SqlRuleAction requires a SqlExpression element.");
                }
                action = new SqlRuleAction(expression, ParseSqlParameters(actionElement));
            }
        }

        return (ParseFilter(filterElement), action, name);
    }

    private static RuleFilter ParseFilter(XElement? filterElement)
    {
        if (filterElement is null) return TrueFilter.Instance;

        var typeValue = filterElement.Attribute(AtomXml.Xsi + "type")?.Value ?? "TrueFilter";
        var colon = typeValue.IndexOf(':');
        var filterType = colon >= 0 ? typeValue[(colon + 1)..] : typeValue;

        switch (filterType)
        {
            case "TrueFilter":
                return TrueFilter.Instance;
            case "FalseFilter":
                return FalseFilter.Instance;
            case "SqlFilter":
            {
                var expression = filterElement.Elements().FirstOrDefault(e => e.Name.LocalName == "SqlExpression")?.Value
                    ?? throw new FormatException("SqlFilter requires a SqlExpression element.");
                // Common degenerate SQL expressions collapse to the constant filters, same as Azure.
                var trimmed = expression.Trim();
                if (trimmed == "1=1") return TrueFilter.Instance;
                if (trimmed == "1=0") return FalseFilter.Instance;
                return new SqlFilter(expression, ParseSqlParameters(filterElement));
            }
            case "CorrelationFilter":
            {
                string? Field(string localName) =>
                    filterElement.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                var propertiesElement = filterElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
                if (propertiesElement is not null)
                {
                    foreach (var pair in propertiesElement.Elements().Where(e => e.Name.LocalName == "KeyValueOfstringanyType"))
                    {
                        var key = pair.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
                        var valueElement = pair.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
                        if (key is null || valueElement is null) continue;
                        properties[key] = ParseCorrelationValue(valueElement);
                    }
                }

                return new CorrelationFilter
                {
                    CorrelationId = Field("CorrelationId"),
                    MessageId = Field("MessageId"),
                    To = Field("To"),
                    ReplyTo = Field("ReplyTo"),
                    Subject = Field("Label"),
                    SessionId = Field("SessionId"),
                    ReplyToSessionId = Field("ReplyToSessionId"),
                    ContentType = Field("ContentType"),
                    Properties = properties,
                };
            }
            default:
                throw new FormatException($"Unsupported filter type '{filterType}'.");
        }
    }

    /// <summary>Parse a SqlFilter/SqlRuleAction Parameters element (KeyValueOfstringanyType
    /// pairs, same typed-value shape as CorrelationFilter Properties). Null when absent.</summary>
    private static Dictionary<string, object?>? ParseSqlParameters(XElement parent)
    {
        var parametersElement = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "Parameters");
        if (parametersElement is null) return null;

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in parametersElement.Elements().Where(e => e.Name.LocalName == "KeyValueOfstringanyType"))
        {
            var key = pair.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
            var valueElement = pair.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
            if (key is null || valueElement is null) continue;
            parameters[key] = ParseCorrelationValue(valueElement);
        }
        return parameters;
    }

    private static XElement? SqlParametersElement(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.Count == 0) return null;
        var element = new XElement(Sb + "Parameters");
        foreach (var (key, value) in parameters)
        {
            element.Add(CorrelationProperty(key, value));
        }
        return element;
    }

    private static object? ParseCorrelationValue(XElement valueElement)
    {
        var typeValue = valueElement.Attribute(AtomXml.Xsi + "type")?.Value;
        var colon = typeValue?.IndexOf(':') ?? -1;
        var xsType = typeValue is null ? "string" : colon >= 0 ? typeValue[(colon + 1)..] : typeValue;
        var text = valueElement.Value;
        return xsType switch
        {
            "boolean" => XmlConvert.ToBoolean(text),
            "int" => XmlConvert.ToInt32(text),
            "long" => XmlConvert.ToInt64(text),
            "double" => XmlConvert.ToDouble(text),
            "dateTime" => XmlConvert.ToDateTimeOffset(text),
            _ => text,
        };
    }
}
