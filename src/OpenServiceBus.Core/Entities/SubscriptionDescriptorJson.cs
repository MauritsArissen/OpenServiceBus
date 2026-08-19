using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Serialization of <see cref="SubscriptionDescriptor"/> snapshots for persistent stores, so
/// subscription settings (sessions, forwarding, auto-delete, metadata, ...) survive a broker
/// restart instead of being partially synthesized back from the backing queue's snapshot.
/// </summary>
public static class SubscriptionDescriptorJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(SubscriptionDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, Options);

    public static SubscriptionDescriptor? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SubscriptionDescriptor>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
