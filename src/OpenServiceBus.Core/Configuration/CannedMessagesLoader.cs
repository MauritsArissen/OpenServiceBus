using OpenServiceBus.Core.Entities;
using System.Text.Json;

namespace OpenServiceBus.Core.Configuration;

/// <summary>
/// Reads a <c>canned-messages.json</c> from disk or a JSON string.
/// </summary>
public static class CannedMessagesLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public sealed record LoadResult(IReadOnlyList<CannedMessage> CannedMessages);

    public static LoadResult LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Canned messages config not found: {path}", path);
        }
        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    public static LoadResult LoadFromJson(string json)
    {
        var cannedMessages = JsonSerializer.Deserialize<CannedMessage[]>(json, JsonOptions)
            ?? throw new InvalidDataException("Canned messages config did not deserialize to a valid CannedMessage array.");

        return new LoadResult(cannedMessages);
    }
}
