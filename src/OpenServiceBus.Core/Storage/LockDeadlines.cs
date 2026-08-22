namespace OpenServiceBus.Core.Storage;

public static class LockDeadlines
{
    public static DateTimeOffset Advance(DateTimeOffset previous, DateTimeOffset candidate)
    {
        var previousMs = previous.ToUnixTimeMilliseconds();
        return candidate.ToUnixTimeMilliseconds() > previousMs
            ? candidate
            : DateTimeOffset.FromUnixTimeMilliseconds(previousMs + 1);
    }
}
