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

    public static DateTime ToWire(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
