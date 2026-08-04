namespace OpenServiceBus.Core.Entities;

public static class EntityValidation
{
    /// <summary>Service Bus's minimum for <c>AutoDeleteOnIdle</c>.</summary>
    public static readonly TimeSpan MinimumAutoDeleteOnIdle = TimeSpan.FromMinutes(5);

    /// <exception cref="InvalidOperationException">The value is below the 5-minute minimum.</exception>
    public static void EnsureAutoDeleteOnIdle(TimeSpan? value, string entityLabel)
    {
        if (value is { } idle && idle < MinimumAutoDeleteOnIdle)
        {
            throw new InvalidOperationException(
                $"The value for AutoDeleteOnIdle on '{entityLabel}' must be at least 5 minutes (was {idle}).");
        }
    }
}
