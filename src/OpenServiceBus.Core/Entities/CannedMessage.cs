namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Configuration for the canned message entity.
/// </summary>
public sealed record CannedMessage
{
    public required string Name { get; init; }

    public required string TopicOrQueue { get; set; }

    public required SendRequest Message { get; set; }
}

public sealed record SendRequest(
    string ConnectionString,
    string Queue,
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
    DateTimeOffset? ScheduledEnqueueTime,
    Dictionary<string, string>? Properties,
    int? Count,
    string? Strategy);
