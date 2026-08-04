using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Serialization of <see cref="QueueDescriptor"/> snapshots for persistent stores, so
/// entity settings (status, lock duration, ...) survive a broker restart.
/// </summary>
public static class QueueDescriptorJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(QueueDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, Options);

    public static QueueDescriptor? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<QueueDescriptor>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
