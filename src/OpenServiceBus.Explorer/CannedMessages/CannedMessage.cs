namespace OpenServiceBus.Explorer.CannedMessages;

public sealed record CannedMessage(
    string Name,
    string? TargetEntity,
    string? Body,
    string? MessageId,
    string? CorrelationId,
    string? Subject,
    string? ContentType,
    string? ReplyTo,
    string? To,
    string? SessionId,
    string? PartitionKey,
    int? TimeToLiveSeconds,
    int? ScheduledDelaySeconds,
    Dictionary<string, string>? Properties,
    int? Count,
    string? Strategy);
