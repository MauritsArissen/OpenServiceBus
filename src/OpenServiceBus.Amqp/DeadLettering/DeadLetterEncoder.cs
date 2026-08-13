using Amqp;
using Amqp.Framing;
using Amqp.Types;

namespace OpenServiceBus.Amqp.DeadLettering;

/// <summary>
/// Round-trips a stored AMQP-encoded message, layering on the Service Bus dead-letter headers
/// (<c>DeadLetterReason</c> / <c>DeadLetterErrorDescription</c> application-properties and
/// <c>x-opt-deadletter-source</c> message-annotation) so it can be enqueued to a DLQ.
/// Used by both <see cref="QueueReceiverSource"/> (explicit reject + max-delivery) and
/// <see cref="TtlExpirationService"/> (background TTL sweep).
/// </summary>
internal static class DeadLetterEncoder
{
    public const string DeadLetterReasonHeader = "DeadLetterReason";
    public const string DeadLetterErrorDescriptionHeader = "DeadLetterErrorDescription";
    public static readonly Symbol DeadLetterSourceSymbol = new("x-opt-deadletter-source");

    public static byte[] AppendDeadLetterHeaders(
        byte[] originalEncoded,
        string sourceQueue,
        string? reason,
        string? description,
        IReadOnlyDictionary<string, object?>? propertiesToModify = null)
    {
        var buffer = new ByteBuffer(originalEncoded, 0, originalEncoded.Length, originalEncoded.Length);
        var msg = Message.Decode(buffer);

        msg.ApplicationProperties ??= new ApplicationProperties();
        // PropertiesToModify from the dead-letter call merge first; the broker-stamped
        // reason/description headers win on a key collision, matching Azure.
        if (propertiesToModify is not null)
        {
            foreach (var (key, value) in propertiesToModify)
            {
                msg.ApplicationProperties[key] = value;
            }
        }
        if (reason is not null) msg.ApplicationProperties[DeadLetterReasonHeader] = reason;
        if (description is not null) msg.ApplicationProperties[DeadLetterErrorDescriptionHeader] = description;

        msg.MessageAnnotations ??= new MessageAnnotations();
        msg.MessageAnnotations.Map[DeadLetterSourceSymbol] = sourceQueue;

        var encoded = msg.Encode();
        var copy = new byte[encoded.Length];
        Array.Copy(encoded.Buffer, encoded.Offset, copy, 0, encoded.Length);
        return copy;
    }
}
