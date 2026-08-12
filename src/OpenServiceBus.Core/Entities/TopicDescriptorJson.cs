using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Serialization of <see cref="TopicDescriptor"/> snapshots for persistent stores, so topic
/// settings (status, TTL, duplicate detection, ...) survive a broker restart instead of being
/// synthesized back with defaults from the subscription backing queues.
/// </summary>
public static class TopicDescriptorJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(TopicDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, Options);

    public static TopicDescriptor? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TopicDescriptor>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
