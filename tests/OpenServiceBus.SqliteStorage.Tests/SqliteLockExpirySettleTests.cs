using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.SqliteStorage;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// SQLite mirror of the deadline-based settle refusal (issue #52).
/// </summary>
public class SqliteLockExpirySettleTests
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(1);

    private static async Task<(SqliteMessageStore Store, FakeTimeProvider Clock, Guid Token)> LockOneAsync()
    {
        var clock = new FakeTimeProvider();
        var store = new SqliteMessageStore(
            new SqliteStorageOptions { DataSource = ":memory:" }, clock, NullLogger<SqliteMessageStore>.Instance);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [0x01]);
        var locked = await store.TryDequeueAsync("q", LockDuration);
        locked.ShouldNotBeNull();
        return (store, clock, locked.LockToken);
    }

    [Fact]
    public async Task Settles_AfterTheLockDeadline_AllFail()
    {
        var (store, clock, token) = await LockOneAsync();
        clock.Advance(LockDuration + TimeSpan.FromSeconds(1));

        (await store.TryCompleteAsync("q", token)).ShouldBeFalse();
        (await store.TryAbandonAsync("q", token)).ShouldBeFalse();
        (await store.TryDeferAsync("q", token)).ShouldBeFalse();
        (await store.TryRemoveLockedAsync("q", token)).ShouldBeNull();
        (await store.TryGetLockedAsync("q", token)).ShouldBeNull();
        (await store.TryUpdateLockedPayloadAsync("q", token, [0xFF])).ShouldBeFalse();
        (await store.TryRenewLockAsync("q", token, LockDuration)).ShouldBeNull();

        store.ExpireLocks("q", clock.GetUtcNow()).ShouldBe(1);
        var redelivered = await store.TryDequeueAsync("q", LockDuration);
        redelivered.ShouldNotBeNull();
        redelivered.Message.DeliveryCount.ShouldBe(1);
        await store.DisposeAsync();
    }

    [Fact]
    public async Task Settle_JustBeforeTheDeadline_StillSucceeds()
    {
        var (store, clock, token) = await LockOneAsync();
        clock.Advance(LockDuration - TimeSpan.FromSeconds(1));

        (await store.TryCompleteAsync("q", token)).ShouldBeTrue();
        await store.DisposeAsync();
    }
}
