using System.Text.Json;

namespace OpenServiceBus.Explorer.CannedMessages;

/// <summary>
/// Optional file backing for the canned message library, pointed at by
/// OSB_EXPLORER_CANNED_FILE. The file is the import/export format verbatim (a JSON array
/// of canned messages, camelCase, indented) so a team can commit it next to their compose
/// file: the Explorer loads it at startup, writes UI edits back when the file is
/// writable, and a reset re-reads it fresh - picking up whatever a git pull changed.
/// Without the variable the library stays purely in-memory, exactly as before.
/// </summary>
public sealed class CannedMessageFileStore
{
    public const string PathSetting = "OSB_EXPLORER_CANNED_FILE";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger<CannedMessageFileStore> _logger;

    public CannedMessageFileStore(IConfiguration configuration, ILogger<CannedMessageFileStore> logger)
    {
        _logger = logger;
        var path = configuration[PathSetting];
        FilePath = string.IsNullOrWhiteSpace(path) ? null : path;
        IsWritable = FilePath is not null && ProbeWritable(FilePath);
    }

    public string? FilePath { get; }

    public bool IsConfigured => FilePath is not null;

    public bool IsWritable { get; private set; }

    public IReadOnlyList<CannedMessage>? TryLoad()
    {
        if (FilePath is null) return null;
        try
        {
            if (!File.Exists(FilePath))
            {
                _logger.LogInformation("Canned message file {Path} does not exist yet - starting with an empty library", FilePath);
                return [];
            }
            var loaded = JsonSerializer.Deserialize<List<CannedMessage>>(File.ReadAllText(FilePath), Json) ?? [];
            var valid = loaded.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToList();
            if (valid.Count < loaded.Count)
            {
                _logger.LogWarning("Canned message file {Path}: skipped {Count} entry/entries without a name", FilePath, loaded.Count - valid.Count);
            }
            _logger.LogInformation("Loaded {Count} canned message(s) from {Path} ({Mode})", valid.Count, FilePath, IsWritable ? "writable" : "read-only");
            return valid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not load canned messages from {Path} - starting with an empty library", FilePath);
            return [];
        }
    }

    public bool TrySave(IReadOnlyList<CannedMessage> messages)
    {
        if (FilePath is null || !IsWritable) return false;
        try
        {
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(messages, Json) + Environment.NewLine);
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsWritable = false;
            _logger.LogWarning(ex, "Could not write canned messages to {Path} - edits stay in memory until restart", FilePath);
            return false;
        }
    }

    public static string Serialize(IReadOnlyList<CannedMessage> messages) =>
        JsonSerializer.Serialize(messages, Json) + Environment.NewLine;

    private bool ProbeWritable(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                return true;
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null && !Directory.Exists(directory)) return false;
            var probePath = path + ".probe";
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write)) { }
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Canned message file {Path} is read-only - UI edits will stay in memory", path);
            return false;
        }
    }
}
