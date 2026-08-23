namespace OpenServiceBus.Explorer.CannedMessages;

public sealed class CannedMessageLibrary
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CannedMessage> _messages = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CannedMessage> _defaults = [];

    public void SeedDefaults(IEnumerable<CannedMessage> defaults)
    {
        lock (_sync)
        {
            _defaults = defaults.ToList();
            _messages.Clear();
            foreach (var message in _defaults)
            {
                _messages[message.Name] = message;
            }
        }
    }

    public IReadOnlyList<CannedMessage> List()
    {
        lock (_sync)
        {
            return _messages.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public CannedMessage? Get(string name)
    {
        lock (_sync)
        {
            return _messages.GetValueOrDefault(name);
        }
    }

    public bool TryCreate(CannedMessage message)
    {
        lock (_sync)
        {
            return _messages.TryAdd(message.Name, message);
        }
    }

    public UpdateResult Update(string existingName, CannedMessage message)
    {
        lock (_sync)
        {
            if (!_messages.ContainsKey(existingName)) return UpdateResult.NotFound;
            var renaming = !string.Equals(existingName, message.Name, StringComparison.OrdinalIgnoreCase);
            if (renaming && _messages.ContainsKey(message.Name)) return UpdateResult.NameConflict;
            _messages.Remove(existingName);
            _messages[message.Name] = message;
            return UpdateResult.Updated;
        }
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            return _messages.Remove(name);
        }
    }

    public CannedMessage? Duplicate(string name)
    {
        lock (_sync)
        {
            if (!_messages.TryGetValue(name, out var source)) return null;
            var copyName = $"{source.Name} (copy)";
            for (var i = 2; _messages.ContainsKey(copyName); i++)
            {
                copyName = $"{source.Name} (copy {i})";
            }
            var copy = source with { Name = copyName };
            _messages[copyName] = copy;
            return copy;
        }
    }

    public ImportSummary Import(IReadOnlyList<CannedMessage> messages, bool replaceConflicts)
    {
        lock (_sync)
        {
            int added = 0, replaced = 0, skipped = 0;
            foreach (var message in messages)
            {
                if (_messages.ContainsKey(message.Name))
                {
                    if (replaceConflicts)
                    {
                        _messages[message.Name] = message;
                        replaced++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    _messages[message.Name] = message;
                    added++;
                }
            }
            return new ImportSummary(added, replaced, skipped);
        }
    }

    public IReadOnlyList<string> ConflictsWith(IEnumerable<string> names)
    {
        lock (_sync)
        {
            return names.Where(_messages.ContainsKey).ToList();
        }
    }

    public void ResetToDefaults()
    {
        lock (_sync)
        {
            _messages.Clear();
            foreach (var message in _defaults)
            {
                _messages[message.Name] = message;
            }
        }
    }
}

public enum UpdateResult { Updated, NotFound, NameConflict }

public sealed record ImportSummary(int Added, int Replaced, int Skipped);
