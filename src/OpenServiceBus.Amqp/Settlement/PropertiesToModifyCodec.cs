using Amqp;
using Amqp.Framing;
using Amqp.Types;

namespace OpenServiceBus.Amqp.Settlement;

/// <summary>
/// Decodes the PropertiesToModify map the SDK settlement overloads carry and merges it into
/// a stored message's application properties (last-write-wins per key), so redeliveries,
/// deferred retrievals and DLQ copies see the modified properties - matching real Service Bus.
///
/// Wire shapes: abandon/defer arrive as a Modified outcome whose message-annotations field
/// holds the map; dead-letter packs the entries into the Rejected error info alongside
/// DeadLetterReason/DeadLetterErrorDescription; the $management update-disposition request
/// carries a "properties-to-modify" map. Keys arrive as Symbol (.NET) or plain string
/// (proton-j), so maps are enumerated entry-wise - the typed Fields indexer rejects
/// non-Symbol keys.
/// </summary>
public static class PropertiesToModifyCodec
{
    public static IReadOnlyDictionary<string, object?>? FromModified(Modified modified) =>
        FromMap(modified.MessageAnnotations);

    /// <summary>
    /// Splits a dead-letter disposition's error info into the reason/description pair and
    /// the remaining entries, which the SDKs use to transport PropertiesToModify.
    /// </summary>
    public static (string? Reason, string? Description, IReadOnlyDictionary<string, object?>? Properties)
        FromRejected(Rejected rejected)
    {
        if (rejected.Error?.Info is null) return (null, null, null);
        string? reason = null;
        string? description = null;
        Dictionary<string, object?>? properties = null;
        foreach (KeyValuePair<object, object> entry in rejected.Error.Info)
        {
            var key = entry.Key?.ToString();
            switch (key)
            {
                case null:
                    break;
                case "DeadLetterReason":
                    reason = entry.Value as string;
                    break;
                case "DeadLetterErrorDescription":
                    description = entry.Value as string;
                    break;
                default:
                    (properties ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = entry.Value;
                    break;
            }
        }
        return (reason, description, properties);
    }

    public static IReadOnlyDictionary<string, object?>? FromMap(object? map)
    {
        if (map is not Map fields) return null;
        Dictionary<string, object?>? properties = null;
        foreach (KeyValuePair<object, object> entry in fields)
        {
            if (entry.Key?.ToString() is not { } key) continue;
            (properties ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = entry.Value;
        }
        return properties;
    }

    public static byte[] MergeIntoEncoded(byte[] encodedMessage, IReadOnlyDictionary<string, object?> properties)
    {
        var buffer = new ByteBuffer(encodedMessage, 0, encodedMessage.Length, encodedMessage.Length);
        var msg = Message.Decode(buffer);
        msg.ApplicationProperties ??= new ApplicationProperties();
        foreach (var (key, value) in properties)
        {
            msg.ApplicationProperties[key] = value;
        }
        var encoded = msg.Encode();
        var copy = new byte[encoded.Length];
        Array.Copy(encoded.Buffer, encoded.Offset, copy, 0, encoded.Length);
        return copy;
    }
}
