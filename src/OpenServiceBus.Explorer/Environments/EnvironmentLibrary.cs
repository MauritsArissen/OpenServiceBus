using System.Text.Json;

namespace OpenServiceBus.Explorer.Environments;

/// <summary>
/// Named environments, one library per Explorer backend - same lifecycle as the canned
/// message library: optionally file-backed (OSB_EXPLORER_ENVIRONMENTS_FILE, a JSON array
/// in Postman's environment-export shape), write-back when writable, reset re-reads the
/// file. Which environment is ACTIVE is a per-browser choice (localStorage), sent along
/// with each send request - the library only stores the sets.
/// </summary>
public sealed class EnvironmentLibrary
{
    public const string PathSetting = "OSB_EXPLORER_ENVIRONMENTS_FILE";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _sync = new();
    private readonly Dictionary<string, ExplorerEnvironment> _environments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EnvironmentLibrary>? _logger;
    private IReadOnlyList<ExplorerEnvironment> _defaults = [];

    public EnvironmentLibrary()
    {
    }

    public EnvironmentLibrary(IConfiguration configuration, ILogger<EnvironmentLibrary> logger)
    {
        _logger = logger;
        var path = configuration[PathSetting];
        FilePath = string.IsNullOrWhiteSpace(path) ? null : path;
        IsWritable = FilePath is not null && ProbeWritable(FilePath);
        if (FilePath is not null && TryLoad() is { } loaded)
        {
            foreach (var environment in loaded)
            {
                _environments[environment.Name] = environment;
            }
        }
    }

    public string? FilePath { get; }

    public bool IsConfigured => FilePath is not null;

    public bool IsWritable { get; private set; }

    public IReadOnlyList<ExplorerEnvironment> List()
    {
        lock (_sync)
        {
            return _environments.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public ExplorerEnvironment? Get(string name)
    {
        lock (_sync)
        {
            return _environments.GetValueOrDefault(name);
        }
    }

    public bool TryCreate(ExplorerEnvironment environment)
    {
        lock (_sync)
        {
            if (!_environments.TryAdd(environment.Name, environment)) return false;
            Persist();
            return true;
        }
    }

    public UpdateOutcome Update(string existingName, ExplorerEnvironment environment)
    {
        lock (_sync)
        {
            if (!_environments.ContainsKey(existingName)) return UpdateOutcome.NotFound;
            var renaming = !string.Equals(existingName, environment.Name, StringComparison.OrdinalIgnoreCase);
            if (renaming && _environments.ContainsKey(environment.Name)) return UpdateOutcome.NameConflict;
            _environments.Remove(existingName);
            _environments[environment.Name] = environment;
            Persist();
            return UpdateOutcome.Updated;
        }
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            if (!_environments.Remove(name)) return false;
            Persist();
            return true;
        }
    }

    public ExplorerEnvironment? Duplicate(string name)
    {
        lock (_sync)
        {
            if (!_environments.TryGetValue(name, out var source)) return null;
            var copyName = $"{source.Name} (copy)";
            for (var i = 2; _environments.ContainsKey(copyName); i++)
            {
                copyName = $"{source.Name} (copy {i})";
            }
            var copy = source with { Name = copyName, Values = [.. source.Values] };
            _environments[copyName] = copy;
            Persist();
            return copy;
        }
    }

    public (int Added, int Replaced, int Skipped) Import(IReadOnlyList<ExplorerEnvironment> environments, bool replaceConflicts)
    {
        lock (_sync)
        {
            int added = 0, replaced = 0, skipped = 0;
            foreach (var environment in environments)
            {
                if (_environments.ContainsKey(environment.Name))
                {
                    if (replaceConflicts)
                    {
                        _environments[environment.Name] = environment;
                        replaced++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    _environments[environment.Name] = environment;
                    added++;
                }
            }
            if (added + replaced > 0) Persist();
            return (added, replaced, skipped);
        }
    }

    public IReadOnlyList<string> ConflictsWith(IEnumerable<string> names)
    {
        lock (_sync)
        {
            return names.Where(_environments.ContainsKey).ToList();
        }
    }

    public void ResetToDefaults()
    {
        lock (_sync)
        {
            var baseline = IsConfigured ? TryLoad() ?? [] : _defaults;
            _environments.Clear();
            foreach (var environment in baseline)
            {
                _environments[environment.Name] = environment;
            }
        }
    }

    public static string Serialize(IReadOnlyList<ExplorerEnvironment> environments) =>
        JsonSerializer.Serialize(environments, Json) + Environment.NewLine;

    private IReadOnlyList<ExplorerEnvironment>? TryLoad()
    {
        if (FilePath is null) return null;
        try
        {
            if (!File.Exists(FilePath)) return [];
            var loaded = JsonSerializer.Deserialize<List<ExplorerEnvironment>>(File.ReadAllText(FilePath), Json) ?? [];
            var valid = loaded
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => e with { Values = e.Values ?? [] })
                .ToList();
            _logger?.LogInformation("Loaded {Count} environment(s) from {Path} ({Mode})", valid.Count, FilePath, IsWritable ? "writable" : "read-only");
            return valid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning(ex, "Could not load environments from {Path} - starting with an empty library", FilePath);
            return [];
        }
    }

    private void Persist()
    {
        if (FilePath is null || !IsWritable) return;
        try
        {
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, Serialize(_environments.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsWritable = false;
            _logger?.LogWarning(ex, "Could not write environments to {Path} - edits stay in memory until restart", FilePath);
        }
    }

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
            _logger?.LogInformation(ex, "Environments file {Path} is read-only - UI edits will stay in memory", path);
            return false;
        }
    }
}

public enum UpdateOutcome { Updated, NotFound, NameConflict }
