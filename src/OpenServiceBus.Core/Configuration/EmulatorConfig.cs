using System.Text.Json.Serialization;

namespace OpenServiceBus.Core.Configuration;

/// <summary>
/// Root document matching the Microsoft Azure Service Bus emulator's <c>config.json</c> format.
/// OpenServiceBus parses the same shape so config files can be lifted from existing emulator
/// projects without modification.
/// </summary>
public sealed class EmulatorConfig
{
    [JsonPropertyName("UserConfig")]
    public UserConfigSection UserConfig { get; set; } = new();
}

public sealed class UserConfigSection
{
    [JsonPropertyName("Namespaces")]
    public List<NamespaceConfig> Namespaces { get; set; } = [];

    [JsonPropertyName("Logging")]
    public LoggingConfig? Logging { get; set; }
}

public sealed class NamespaceConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Queues")]
    public List<QueueConfig> Queues { get; set; } = [];

    [JsonPropertyName("Topics")]
    public List<TopicConfig>? Topics { get; set; }
}

public sealed class QueueConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Properties")]
    public QueueProperties? Properties { get; set; }
}

public sealed class QueueProperties
{
    /// <summary>ISO 8601 duration (e.g. <c>PT1M</c>) - converted to <see cref="TimeSpan"/>.</summary>
    [JsonPropertyName("LockDuration")]
    public string? LockDuration { get; set; }

    [JsonPropertyName("MaxDeliveryCount")]
    public int? MaxDeliveryCount { get; set; }

    /// <summary>ISO 8601 duration - null means messages never expire.</summary>
    [JsonPropertyName("DefaultMessageTimeToLive")]
    public string? DefaultMessageTimeToLive { get; set; }

    [JsonPropertyName("DeadLetteringOnMessageExpiration")]
    public bool? DeadLetteringOnMessageExpiration { get; set; }

    [JsonPropertyName("RequiresSession")]
    public bool? RequiresSession { get; set; }

    [JsonPropertyName("RequiresDuplicateDetection")]
    public bool? RequiresDuplicateDetection { get; set; }

    [JsonPropertyName("DuplicateDetectionHistoryTimeWindow")]
    public string? DuplicateDetectionHistoryTimeWindow { get; set; }

    [JsonPropertyName("ForwardTo")]
    public string? ForwardTo { get; set; }

    [JsonPropertyName("ForwardDeadLetteredMessagesTo")]
    public string? ForwardDeadLetteredMessagesTo { get; set; }
}

public sealed class TopicConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Properties")]
    public TopicProperties? Properties { get; set; }

    [JsonPropertyName("Subscriptions")]
    public List<SubscriptionConfig>? Subscriptions { get; set; }
}

public sealed class TopicProperties
{
    /// <summary>ISO 8601 duration - null means messages never expire.</summary>
    [JsonPropertyName("DefaultMessageTimeToLive")]
    public string? DefaultMessageTimeToLive { get; set; }
}

public sealed class SubscriptionConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Properties")]
    public SubscriptionProperties? Properties { get; set; }

    [JsonPropertyName("Rules")]
    public List<RuleConfig>? Rules { get; set; }
}

public sealed class SubscriptionProperties
{
    [JsonPropertyName("LockDuration")]
    public string? LockDuration { get; set; }

    [JsonPropertyName("MaxDeliveryCount")]
    public int? MaxDeliveryCount { get; set; }

    [JsonPropertyName("DefaultMessageTimeToLive")]
    public string? DefaultMessageTimeToLive { get; set; }

    [JsonPropertyName("DeadLetteringOnMessageExpiration")]
    public bool? DeadLetteringOnMessageExpiration { get; set; }

    [JsonPropertyName("RequiresSession")]
    public bool? RequiresSession { get; set; }

    [JsonPropertyName("ForwardTo")]
    public string? ForwardTo { get; set; }

    [JsonPropertyName("ForwardDeadLetteredMessagesTo")]
    public string? ForwardDeadLetteredMessagesTo { get; set; }
}

public sealed class RuleConfig
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Properties")]
    public RuleProperties? Properties { get; set; }
}

/// <summary>
/// Rule shape matching the Microsoft emulator: a <c>FilterType</c> discriminator plus the
/// payload object for whichever filter was picked. Only the field matching the discriminator
/// is read; the others are tolerated when present so config files round-trip cleanly.
/// </summary>
public sealed class RuleProperties
{
    /// <summary>One of <c>Sql</c>, <c>Correlation</c>, <c>True</c>, <c>False</c>.</summary>
    [JsonPropertyName("FilterType")]
    public string? FilterType { get; set; }

    [JsonPropertyName("SqlFilter")]
    public SqlFilterConfig? SqlFilter { get; set; }

    [JsonPropertyName("CorrelationFilter")]
    public CorrelationFilterConfig? CorrelationFilter { get; set; }
}

public sealed class SqlFilterConfig
{
    [JsonPropertyName("SqlExpression")]
    public string? SqlExpression { get; set; }
}

public sealed class CorrelationFilterConfig
{
    [JsonPropertyName("MessageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("CorrelationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("Subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("To")]
    public string? To { get; set; }

    [JsonPropertyName("ReplyTo")]
    public string? ReplyTo { get; set; }

    [JsonPropertyName("ReplyToSessionId")]
    public string? ReplyToSessionId { get; set; }

    [JsonPropertyName("SessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("ContentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("Properties")]
    public Dictionary<string, string>? Properties { get; set; }
}

public sealed class LoggingConfig
{
    [JsonPropertyName("Type")]
    public string? Type { get; set; }
}
