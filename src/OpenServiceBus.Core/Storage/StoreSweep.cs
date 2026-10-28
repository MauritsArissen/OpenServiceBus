namespace OpenServiceBus.Core.Storage;

/// <summary>
/// Result of a store-wide sweep (<see cref="IMessageStore.ExpireLocksAll"/>,
/// <see cref="IMessageStore.ExpireMessagesAll"/>, <see cref="IMessageStore.ActivateScheduledAll"/>).
/// <see cref="ByQueue"/> carries the per-queue outcome for every queue the sweep touched -
/// queues where nothing happened are absent, so an idle sweep returns an empty dictionary.
/// <see cref="HasPending"/> is true while the store still holds rows the sweep could act on
/// later (unexpired locks, messages with a TTL, future-dated scheduled messages); the
/// background sweepers use it to back their timers off when the broker is idle, so a store
/// that cannot answer cheaply should report true.
/// </summary>
public sealed record StoreSweep<T>(IReadOnlyDictionary<string, T> ByQueue, bool HasPending)
{
    public static readonly IReadOnlyDictionary<string, T> EmptyByQueue =
        new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

    public static StoreSweep<T> Empty(bool hasPending) => new(EmptyByQueue, hasPending);
}
