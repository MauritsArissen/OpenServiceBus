using System.Collections.Concurrent;

namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Last-activity timestamps per entity, feeding <c>AutoDeleteOnIdle</c> enforcement.
/// Activity = sends (including fan-out copies landing), successful receives, peeks, and
/// link attaches. Driven by the injected <see cref="TimeProvider"/> so fake-time tests
/// can time-travel the idle window. See docs/Auto-Delete-On-Idle.md.
/// </summary>
public sealed class EntityActivityTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActivity = new(StringComparer.OrdinalIgnoreCase);

    public EntityActivityTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void Touch(string entityName) => _lastActivity[entityName] = _timeProvider.GetUtcNow();

    public DateTimeOffset? LastActivity(string entityName) =>
        _lastActivity.TryGetValue(entityName, out var at) ? at : null;

    /// <summary>Record activity only if the entity has no entry yet (idle-clock baseline).</summary>
    public DateTimeOffset TouchIfUnseen(string entityName) =>
        _lastActivity.GetOrAdd(entityName, _ => _timeProvider.GetUtcNow());

    public void Forget(string entityName) => _lastActivity.TryRemove(entityName, out _);
}
