using Amqp;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Amqp.Topics;

/// <summary>
/// Builds the <see cref="MessageFilterContext"/> a topic fan-out evaluates rules against,
/// from either a decoded AMQP message or stored encoded bytes. Shared by the live publish
/// path (<see cref="TopicSenderProcessor"/>) and the scheduled-publish activation sweep,
/// so both evaluate filters identically.
/// </summary>
internal static class AmqpFilterContext
{
    public static MessageFilterContext FromMessage(Message msg, DateTimeOffset enqueuedAt)
    {
        var appProps = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (msg.ApplicationProperties is not null)
        {
            foreach (var key in msg.ApplicationProperties.Map.Keys)
            {
                if (key is null) continue;
                appProps[key.ToString()!] = msg.ApplicationProperties.Map[key];
            }
        }
        return new MessageFilterContext
        {
            MessageId = msg.Properties?.MessageId,
            CorrelationId = msg.Properties?.CorrelationId,
            Subject = msg.Properties?.Subject,
            To = msg.Properties?.To,
            ReplyTo = msg.Properties?.ReplyTo,
            ReplyToSessionId = msg.Properties?.ReplyToGroupId,
            SessionId = msg.Properties?.GroupId,
            ContentType = msg.Properties?.ContentType,
            EnqueuedTimeUtc = enqueuedAt,
            ApplicationProperties = appProps,
        };
    }

    public static MessageFilterContext FromEncoded(byte[] encodedMessage, DateTimeOffset enqueuedAt)
    {
        var buffer = new ByteBuffer(encodedMessage, 0, encodedMessage.Length, encodedMessage.Length);
        return FromMessage(Message.Decode(buffer), enqueuedAt);
    }
}
