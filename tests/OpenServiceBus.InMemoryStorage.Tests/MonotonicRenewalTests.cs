using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class MonotonicRenewalTests
{
    [Fact]
    public async Task TryRenewLockAsync_OnAFrozenClock_StillAdvancesTheDeadline()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1]);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        locked.ShouldNotBeNull();

        // Act
        var first = await store.TryRenewLockAsync("q", locked.LockToken, TimeSpan.FromSeconds(30));
        var second = await store.TryRenewLockAsync("q", locked.LockToken, TimeSpan.FromSeconds(30));

        // Assert
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(locked.LockedUntil.ToUnixTimeMilliseconds());
        second!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(first.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task TryRenewSessionLockAsync_OnAFrozenClock_StillAdvancesTheDeadline()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");
        var accepted = await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30));
        accepted.ShouldNotBeNull();

        // Act
        var first = await store.TryRenewSessionLockAsync("q", "s", TimeSpan.FromSeconds(30));
        var second = await store.TryRenewSessionLockAsync("q", "s", TimeSpan.FromSeconds(30));

        // Assert
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(accepted!.LockedUntil.ToUnixTimeMilliseconds());
        second!.Value.ToUnixTimeMilliseconds().ShouldBeGreaterThan(first.Value.ToUnixTimeMilliseconds());
    }
}
