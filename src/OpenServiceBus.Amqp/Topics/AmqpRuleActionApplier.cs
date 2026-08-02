using Amqp;
using Amqp.Framing;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Amqp.Topics;

/// <summary>
/// Applies a <see cref="SqlRuleAction"/> to an AMQP-encoded message: decode, mutate the
/// properties/application-properties sections through <see cref="ISqlRuleActionTarget"/>,
/// re-encode. Each application starts from the original bytes, so every matching
/// subscription mutates its own copy independently.
/// </summary>
public sealed class AmqpRuleActionApplier : IRuleActionApplier
{
    private readonly TimeProvider _timeProvider;

    public AmqpRuleActionApplier(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public byte[] Apply(byte[] encodedMessage, SqlRuleAction action)
    {
        var buffer = new ByteBuffer(encodedMessage, 0, encodedMessage.Length, encodedMessage.Length);
        var message = Message.Decode(buffer);

        action.Apply(new MessageTarget(message, _timeProvider.GetUtcNow()));

        var reencoded = message.Encode();
        var copy = new byte[reencoded.Length];
        Array.Copy(reencoded.Buffer, reencoded.Offset, copy, 0, reencoded.Length);
        return copy;
    }

    private sealed class MessageTarget(Message message, DateTimeOffset now) : ISqlRuleActionTarget
    {
        public MessageFilterContext BuildFilterContext()
        {
            var appProps = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (message.ApplicationProperties is not null)
            {
                foreach (var key in message.ApplicationProperties.Map.Keys)
                {
                    if (key is null) continue;
                    appProps[key.ToString()!] = message.ApplicationProperties.Map[key];
                }
            }
            return new MessageFilterContext
            {
                MessageId = message.Properties?.MessageId,
                CorrelationId = message.Properties?.CorrelationId,
                Subject = message.Properties?.Subject,
                To = message.Properties?.To,
                ReplyTo = message.Properties?.ReplyTo,
                ReplyToSessionId = message.Properties?.ReplyToGroupId,
                SessionId = message.Properties?.GroupId,
                ContentType = message.Properties?.ContentType,
                EnqueuedTimeUtc = now,
                ApplicationProperties = appProps,
            };
        }

        public void SetSystemProperty(string canonicalName, string? value)
        {
            message.Properties ??= new Properties();
            switch (canonicalName)
            {
                case "Subject": message.Properties.Subject = value; break;
                case "CorrelationId": message.Properties.CorrelationId = value; break;
                case "To": message.Properties.To = value; break;
                case "ReplyTo": message.Properties.ReplyTo = value; break;
                case "ReplyToSessionId": message.Properties.ReplyToGroupId = value; break;
                case "ContentType": message.Properties.ContentType = value; break;
                default:
                    // Unreachable: the parser only admits the writable set above.
                    throw new InvalidOperationException($"System property '{canonicalName}' is not writable.");
            }
        }

        public void SetApplicationProperty(string name, object? value)
        {
            message.ApplicationProperties ??= new ApplicationProperties();
            message.ApplicationProperties.Map[name] = value;
        }

        public void RemoveApplicationProperty(string name)
        {
            message.ApplicationProperties?.Map.Remove(name);
        }
    }
}
