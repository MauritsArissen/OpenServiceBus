using System.Text.Json;

namespace NovaBank.Api.Messaging;

/// <summary>Single JSON dialect for every message body on the bus.</summary>
public static class BusJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
