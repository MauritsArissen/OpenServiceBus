using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.SqliteStorage;

namespace OpenServiceBus.SqliteStorage.Tests;

public class SqliteMonotonicRenewalTests
{
    private static SqliteMessageStore CreateStore(FakeTimeProvider clock) => new(
        new SqliteStorageOptions { DataSource = ":memory:" }, clock, NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task TryRenewLockAsync_OnAFrozenClock_StillAdvancesTheDeadline()
    {
        var clock = new FakeTimeProvider();
        await using var store = CreateStore(clock);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [0x01]);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        locked.ShouldNotBeNull();

        var first = await store.TryRenewLockAsync("q", locked.LockToken, TimeSpan.FromSeconds(30));
        var second = await store.TryRenewLockAsync("q", locked.LockToken, TimeSpan.FromSeconds(30));

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(locked.LockedUntil.ToUnixTimeMilliseconds());
        second!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(first.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task TryRenewSessionLockAsync_OnAFrozenClock_StillAdvancesTheDeadline()
    {
        var clock = new FakeTimeProvider();
        await using var store = CreateStore(clock);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [0x01], sessionId: "s");
        var accepted = await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30));
        accepted.ShouldNotBeNull();

        var first = await store.TryRenewSessionLockAsync("q", "s", TimeSpan.FromSeconds(30));
        var second = await store.TryRenewSessionLockAsync("q", "s", TimeSpan.FromSeconds(30));

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(accepted!.LockedUntil.ToUnixTimeMilliseconds());
        second!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(first.Value.ToUnixTimeMilliseconds());
    }
}
