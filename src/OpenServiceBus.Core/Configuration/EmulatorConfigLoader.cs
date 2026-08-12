using System.Text.Json;
using System.Xml;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Configuration;

/// <summary>
/// Reads a Microsoft-emulator-compatible <c>config.json</c> from disk or a JSON string and
/// projects it onto OpenServiceBus's descriptor types (<see cref="QueueDescriptor"/>,
/// <see cref="TopicDescriptor"/>, <see cref="SubscriptionDescriptor"/>, <see cref="RuleDescriptor"/>).
/// Anything that doesn't parse cleanly becomes a warning string the caller can log; the
/// loader never throws on bad fields, only on a structurally invalid root document.
/// </summary>
public static class EmulatorConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public sealed record LoadResult(
        IReadOnlyList<QueueDescriptor> Queues,
        IReadOnlyList<TopicDescriptor> Topics,
        IReadOnlyList<SubscriptionDescriptor> Subscriptions,
        IReadOnlyList<RuleDescriptor> Rules,
        IReadOnlyList<string> Warnings);

    public static LoadResult LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Bootstrap config not found: {path}", path);
        }
        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    public static LoadResult LoadFromJson(string json)
    {
        var config = JsonSerializer.Deserialize<EmulatorConfig>(json, JsonOptions)
            ?? throw new InvalidDataException("Bootstrap config did not deserialize to a valid EmulatorConfig.");

        var queues = new List<QueueDescriptor>();
        var topics = new List<TopicDescriptor>();
        var subscriptions = new List<SubscriptionDescriptor>();
        var rules = new List<RuleDescriptor>();
        var warnings = new List<string>();

        foreach (var ns in config.UserConfig.Namespaces)
        {
            foreach (var q in ns.Queues)
            {
                if (string.IsNullOrWhiteSpace(q.Name))
                {
                    warnings.Add("Skipping queue with empty Name.");
                    continue;
                }
                queues.Add(ProjectQueue(q, warnings));
            }

            if (ns.Topics is null) continue;
            foreach (var t in ns.Topics)
            {
                if (string.IsNullOrWhiteSpace(t.Name))
                {
                    warnings.Add("Skipping topic with empty Name.");
                    continue;
                }
                topics.Add(ProjectTopic(t, warnings));

                if (t.Subscriptions is null) continue;
                foreach (var s in t.Subscriptions)
                {
                    if (string.IsNullOrWhiteSpace(s.Name))
                    {
                        warnings.Add($"Topic '{t.Name}': skipping subscription with empty Name.");
                        continue;
                    }
                    subscriptions.Add(ProjectSubscription(t.Name, s, warnings));

                    if (s.Rules is null) continue;
                    foreach (var r in s.Rules)
                    {
                        if (string.IsNullOrWhiteSpace(r.Name))
                        {
                            warnings.Add($"Topic '{t.Name}', subscription '{s.Name}': skipping rule with empty Name.");
                            continue;
                        }
                        var rule = ProjectRule(t.Name, s.Name, r, warnings);
                        if (rule is not null) rules.Add(rule);
                    }
                }
            }
        }

        return new LoadResult(queues, topics, subscriptions, rules, warnings);
    }

    private static QueueDescriptor ProjectQueue(QueueConfig q, List<string> warnings)
    {
        var props = q.Properties ?? new QueueProperties();

        return new QueueDescriptor
        {
            Name = q.Name,
            MaxDeliveryCount = props.MaxDeliveryCount ?? new QueueDescriptor { Name = q.Name }.MaxDeliveryCount,
            LockDuration = ParseDuration(props.LockDuration, $"Queue '{q.Name}'.LockDuration", warnings)
                           ?? new QueueDescriptor { Name = q.Name }.LockDuration,
            DefaultMessageTimeToLive = ParseDuration(props.DefaultMessageTimeToLive, $"Queue '{q.Name}'.DefaultMessageTimeToLive", warnings),
            DeadLetteringOnMessageExpiration = props.DeadLetteringOnMessageExpiration ?? false,
            RequiresSession = props.RequiresSession ?? false,
            Status = EntityStatusExtensions.Parse(props.Status),
            AutoDeleteOnIdle = ParseDuration(props.AutoDeleteOnIdle, $"Queue '{q.Name}'.AutoDeleteOnIdle", warnings),
            MaxSizeInMegabytes = props.MaxSizeInMegabytes ?? 1024,
            MaxMessageSizeInKilobytes = props.MaxMessageSizeInKilobytes ?? 256,
            RequiresDuplicateDetection = props.RequiresDuplicateDetection ?? false,
            DuplicateDetectionHistoryTimeWindow = ParseDuration(props.DuplicateDetectionHistoryTimeWindow, $"Queue '{q.Name}'.DuplicateDetectionHistoryTimeWindow", warnings),
            // Auto-forwarding. The router enforces target-exists lazily at runtime so
            // queues and topics may appear in config.json in any order.
            ForwardTo = string.IsNullOrEmpty(props.ForwardTo) ? null : props.ForwardTo,
            ForwardDeadLetteredMessagesTo = string.IsNullOrEmpty(props.ForwardDeadLetteredMessagesTo) ? null : props.ForwardDeadLetteredMessagesTo,
        };
    }

    private static TopicDescriptor ProjectTopic(TopicConfig t, List<string> warnings) =>
        new()
        {
            Name = t.Name,
            DefaultMessageTimeToLive = ParseDuration(t.Properties?.DefaultMessageTimeToLive,
                $"Topic '{t.Name}'.DefaultMessageTimeToLive", warnings),
            Status = EntityStatusExtensions.Parse(t.Properties?.Status),
            AutoDeleteOnIdle = ParseDuration(t.Properties?.AutoDeleteOnIdle, $"Topic '{t.Name}'.AutoDeleteOnIdle", warnings),
            MaxSizeInMegabytes = t.Properties?.MaxSizeInMegabytes ?? 1024,
            MaxMessageSizeInKilobytes = t.Properties?.MaxMessageSizeInKilobytes ?? 256,
            RequiresDuplicateDetection = t.Properties?.RequiresDuplicateDetection ?? false,
            DuplicateDetectionHistoryTimeWindow = ParseDuration(t.Properties?.DuplicateDetectionHistoryTimeWindow,
                $"Topic '{t.Name}'.DuplicateDetectionHistoryTimeWindow", warnings),
        };

    private static SubscriptionDescriptor ProjectSubscription(string topicName, SubscriptionConfig s, List<string> warnings)
    {
        var props = s.Properties ?? new SubscriptionProperties();
        var defaults = new SubscriptionDescriptor { TopicName = topicName, Name = s.Name };

        return new SubscriptionDescriptor
        {
            TopicName = topicName,
            Name = s.Name,
            MaxDeliveryCount = props.MaxDeliveryCount ?? defaults.MaxDeliveryCount,
            LockDuration = ParseDuration(props.LockDuration, $"Subscription '{topicName}/{s.Name}'.LockDuration", warnings)
                           ?? defaults.LockDuration,
            DefaultMessageTimeToLive = ParseDuration(props.DefaultMessageTimeToLive,
                $"Subscription '{topicName}/{s.Name}'.DefaultMessageTimeToLive", warnings),
            DeadLetteringOnMessageExpiration = props.DeadLetteringOnMessageExpiration ?? false,
            RequiresSession = props.RequiresSession ?? false,
            Status = EntityStatusExtensions.Parse(props.Status),
            AutoDeleteOnIdle = ParseDuration(props.AutoDeleteOnIdle, $"Subscription '{topicName}/{s.Name}'.AutoDeleteOnIdle", warnings),
            ForwardTo = string.IsNullOrEmpty(props.ForwardTo) ? null : props.ForwardTo,
            ForwardDeadLetteredMessagesTo = string.IsNullOrEmpty(props.ForwardDeadLetteredMessagesTo)
                ? null : props.ForwardDeadLetteredMessagesTo,
        };
    }

    private static RuleDescriptor? ProjectRule(string topicName, string subscriptionName, RuleConfig r, List<string> warnings)
    {
        var props = r.Properties ?? new RuleProperties();
        var label = $"Rule '{topicName}/{subscriptionName}/{r.Name}'";

        RuleFilter filter;
        switch ((props.FilterType ?? "Sql").ToLowerInvariant())
        {
            case "true":
                filter = TrueFilter.Instance;
                break;
            case "false":
                filter = FalseFilter.Instance;
                break;
            case "sql":
                var expr = props.SqlFilter?.SqlExpression;
                if (string.IsNullOrWhiteSpace(expr))
                {
                    warnings.Add($"{label}: FilterType=Sql but SqlFilter.SqlExpression is empty - skipping.");
                    return null;
                }
                filter = new SqlFilter(expr);
                break;
            case "correlation":
                var cf = props.CorrelationFilter;
                if (cf is null)
                {
                    warnings.Add($"{label}: FilterType=Correlation but no CorrelationFilter payload - skipping.");
                    return null;
                }
                filter = new CorrelationFilter
                {
                    MessageId = cf.MessageId,
                    CorrelationId = cf.CorrelationId,
                    Subject = cf.Subject,
                    To = cf.To,
                    ReplyTo = cf.ReplyTo,
                    ReplyToSessionId = cf.ReplyToSessionId,
                    SessionId = cf.SessionId,
                    ContentType = cf.ContentType,
                    Properties = cf.Properties is null
                        ? new Dictionary<string, object?>(StringComparer.Ordinal)
                        : new Dictionary<string, object?>(
                            cf.Properties.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)),
                            StringComparer.Ordinal),
                };
                break;
            default:
                warnings.Add($"{label}: unknown FilterType '{props.FilterType}' - skipping.");
                return null;
        }

        SqlRuleAction? action = null;
        if (!string.IsNullOrWhiteSpace(props.Action?.SqlExpression))
        {
            try
            {
                action = new SqlRuleAction(props.Action.SqlExpression);
            }
            catch (FormatException ex)
            {
                warnings.Add($"{label}: invalid Action.SqlExpression - {ex.Message} - skipping the action.");
            }
        }

        return new RuleDescriptor
        {
            TopicName = topicName,
            SubscriptionName = subscriptionName,
            Name = r.Name,
            Filter = filter,
            Action = action,
        };
    }

    private static TimeSpan? ParseDuration(string? value, string fieldLabel, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            warnings.Add($"{fieldLabel}: '{value}' is not a valid ISO 8601 duration (e.g. PT1M, PT1H); ignoring.");
            return null;
        }
    }
}
