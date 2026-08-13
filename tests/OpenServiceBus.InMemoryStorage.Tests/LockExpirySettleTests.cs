using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// Settling against an expired lock fails at the DEADLINE, not at the next sweeper pass
/// (issue #52): the settle operations refuse expired entries and leave them for
/// <c>ExpireLocks</c> to return the message to its proper state.
/// </summary>
public class LockExpirySettleTests
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(1);

    private static async Task<(InMemoryMessageStore Store, FakeTimeProvider Clock, Guid Token)> LockOneAsync()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryMessageStore(clock);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [0x01]);
        var locked = await store.TryDequeueAsync("q", LockDuration);
        locked.ShouldNotBeNull();
        return (store, clock, locked.LockToken);
    }

    [Fact]
    public async Task Complete_AfterTheLockDeadline_Fails_AndTheMessageRedelivers()
    {
        var (store, clock, token) = await LockOneAsync();
        clock.Advance(LockDuration + TimeSpan.FromSeconds(1));

        (await store.TryCompleteAsync("q", token)).ShouldBeFalse(
            "the deadline passing IS the loss, even before the sweeper reclaims the entry");

        // The refused settle reclaims the expired lock on the spot, so the message is
        // already available again - no sweeper pass needed.
        store.ExpireLocks("q", clock.GetUtcNow()).ShouldBe(0);
        var redelivered = await store.TryDequeueAsync("q", LockDuration);
        redelivered.ShouldNotBeNull();
        redelivered.Message.DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task AbandonDeferAndRemove_AfterTheLockDeadline_AllFail()
    {
        var (store, clock, token) = await LockOneAsync();
        clock.Advance(LockDuration + TimeSpan.FromSeconds(1));

        (await store.TryAbandonAsync("q", token)).ShouldBeFalse();
        (await store.TryDeferAsync("q", token)).ShouldBeFalse();
        (await store.TryRemoveLockedAsync("q", token)).ShouldBeNull();
        (await store.TryGetLockedAsync("q", token)).ShouldBeNull();
        (await store.TryUpdateLockedPayloadAsync("q", token, [0xFF])).ShouldBeFalse();
    }

    [Fact]
    public async Task Renew_AfterTheLockDeadline_CannotReviveTheLock()
    {
        var (store, clock, token) = await LockOneAsync();
        clock.Advance(LockDuration + TimeSpan.FromSeconds(1));

        (await store.TryRenewLockAsync("q", token, LockDuration)).ShouldBeNull();
    }

    [Fact]
    public async Task Settle_JustBeforeTheDeadline_StillSucceeds()
    {
        var (store, clock, token) = await LockOneAsync();
        clock.Advance(LockDuration - TimeSpan.FromSeconds(1));

        (await store.TryCompleteAsync("q", token)).ShouldBeTrue();
    }
}
