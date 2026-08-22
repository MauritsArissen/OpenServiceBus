using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class SessionsTests
{
    [Fact]
    public async Task EnqueueAsync_WithSessionId_DoesNotMakeMessageVisibleToNonSessionDequeue()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30), cancellationToken: cts.Token);

        // Assert
        locked.ShouldBeNull("a session message must not surface through the regular dequeue path");
        (await store.CountAsync("q")).ShouldBe(1L);
    }

    [Fact]
    public async Task TryAcceptSessionAsync_ThenDequeueFromSession_DeliversTheSessionMessage()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [42], sessionId: "s1");

        // Act
        var sessionLock = await store.TryAcceptSessionAsync("q", "s1", TimeSpan.FromSeconds(30));
        var locked = await store.TryDequeueFromSessionAsync("q", "s1", TimeSpan.FromSeconds(30));

        // Assert
        sessionLock.ShouldNotBeNull();
        sessionLock.SessionId.ShouldBe("s1");
        locked.ShouldNotBeNull();
        locked.Message.EncodedMessage.ShouldBe(new byte[] { 42 });
        locked.Message.SessionId.ShouldBe("s1");
    }

    [Fact]
    public async Task TryAcceptSessionAsync_WhenAlreadyLockedByAnotherReceiver_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s1");

        // Act
        var first = await store.TryAcceptSessionAsync("q", "s1", TimeSpan.FromSeconds(30), linkName: "r1");
        var second = await store.TryAcceptSessionAsync("q", "s1", TimeSpan.FromSeconds(30), linkName: "r2");

        // Assert
        first.ShouldNotBeNull();
        second.ShouldBeNull("only one receiver may hold the session lock at a time");
    }

    [Fact]
    public async Task TryAcceptNextSessionAsync_ReturnsSessionWithMessages_AndSkipsLockedOnes()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s1");
        await store.EnqueueAsync("q", [2], sessionId: "s2");
        await store.TryAcceptSessionAsync("q", "s1", TimeSpan.FromSeconds(30), linkName: "first-holder");

        // Act
        var next = await store.TryAcceptNextSessionAsync("q", TimeSpan.FromSeconds(30), linkName: "second-receiver");

        // Assert
        next.ShouldNotBeNull();
        next.SessionId.ShouldBe("s2", "s1 is already locked so the broker hands out s2");
    }

    [Fact]
    public async Task TryDequeueFromSessionAsync_RepeatedReceives_DeliversInEnqueueOrderWithinSession()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");
        await store.EnqueueAsync("q", [2], sessionId: "s");
        await store.EnqueueAsync("q", [3], sessionId: "s");
        await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30));

        // Act
        var bodies = new List<byte>();
        for (var i = 0; i < 3; i++)
        {
            var l = await store.TryDequeueFromSessionAsync("q", "s", TimeSpan.FromSeconds(30));
            bodies.Add(l!.Message.EncodedMessage[0]);
            await store.TryCompleteAsync("q", l.LockToken);
        }

        // Assert
        bodies.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task SetSessionStateAsync_ThenGet_RoundTripsStateBlob()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");

        // Act
        await store.SetSessionStateAsync("q", "s", new byte[] { 7, 7, 7 });
        var read = await store.GetSessionStateAsync("q", "s");

        // Assert
        read.ShouldBe(new byte[] { 7, 7, 7 });
    }

    [Fact]
    public async Task SetSessionStateAsync_Null_ClearsExistingState()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.SetSessionStateAsync("q", "s", new byte[] { 1 });

        // Act
        await store.SetSessionStateAsync("q", "s", null);

        // Assert
        (await store.GetSessionStateAsync("q", "s")).ShouldBeNull();
    }

    [Fact]
    public async Task TryRenewSessionLockAsync_AfterAccept_ExtendsLockedUntilDeadline()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");
        var lock1 = await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(10));

        // Act
        var newUntil = await store.TryRenewSessionLockAsync("q", "s", TimeSpan.FromSeconds(60));

        // Assert
        newUntil.ShouldNotBeNull();
        newUntil!.Value.ShouldBe(time.GetUtcNow() + TimeSpan.FromSeconds(60));
        newUntil.Value.ShouldBeGreaterThan(lock1!.LockedUntil);
    }

    [Fact]
    public async Task TryRenewSessionLockAsync_FromDifferentLink_ReturnsNull()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");
        await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30), linkName: "r1");

        // Act
        var renewed = await store.TryRenewSessionLockAsync("q", "s", TimeSpan.FromSeconds(60), requestingLinkName: "r2");

        // Assert
        renewed.ShouldBeNull("session lock affinity matches message-lock affinity - cross-link renew is refused");
    }

    [Fact]
    public async Task ReleaseSessionAsync_AfterAccept_PermitsAnotherReceiverToTakeIt()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");
        await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30), linkName: "r1");

        // Act
        await store.ReleaseSessionAsync("q", "s");
        var second = await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30), linkName: "r2");

        // Assert
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task TryAbandonAsync_OnSessionLockedMessage_ReturnsItToTheSameSessionForRedelivery()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s");
        await store.TryAcceptSessionAsync("q", "s", TimeSpan.FromSeconds(30));
        var first = await store.TryDequeueFromSessionAsync("q", "s", TimeSpan.FromSeconds(30));

        // Act
        await store.TryAbandonAsync("q", first!.LockToken);
        var second = await store.TryDequeueFromSessionAsync("q", "s", TimeSpan.FromSeconds(30));

        // Assert
        second.ShouldNotBeNull();
        second.Message.SessionId.ShouldBe("s");
        second.Message.DeliveryCount.ShouldBe(1, "abandon bumped delivery-count");
    }

    [Fact]
    public async Task ListSessions_ReturnsSessionsWithMessagesOrState()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "with-messages");
        await store.SetSessionStateAsync("q", "with-state-only", new byte[] { 9 });

        // Act
        var ids = store.ListSessions("q");

        // Assert
        ids.OrderBy(s => s).ShouldBe(new[] { "with-messages", "with-state-only" });
    }

    [Fact]
    public async Task ListSessions_OrdersBySessionIdOrdinal_AndPagesWithSkipAndTop()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        foreach (var id in new[] { "s-3", "s-1", "s-5", "s-2", "s-4" })
        {
            await store.EnqueueAsync("q", [1], sessionId: id);
        }

        // Act
        var all = store.ListSessions("q");
        var page1 = store.ListSessions("q", skip: 0, top: 2);
        var page2 = store.ListSessions("q", skip: 2, top: 2);
        var page3 = store.ListSessions("q", skip: 4, top: 2);
        var beyond = store.ListSessions("q", skip: 5, top: 2);

        // Assert
        all.ShouldBe(new[] { "s-1", "s-2", "s-3", "s-4", "s-5" }, "ordinal order makes skip paging deterministic");
        page1.ShouldBe(new[] { "s-1", "s-2" });
        page2.ShouldBe(new[] { "s-3", "s-4" });
        page3.ShouldBe(new[] { "s-5" });
        beyond.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListSessions_NonPositiveTopOrNegativeSkip_AreHandled()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s-1");

        // Act + Assert
        store.ListSessions("q", top: 0).ShouldBeEmpty("top 0 asks for an empty page");
        store.ListSessions("q", top: -1).ShouldBeEmpty();
        store.ListSessions("q", skip: -5, top: 10).ShouldBe(new[] { "s-1" }, "negative skip clamps to 0");
    }

    [Fact]
    public async Task ListSessions_WithStateUpdatedAfter_FiltersOnStateUpdateInstant()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var store = new InMemoryMessageStore(clock);
        await store.CreateQueueAsync("q");
        await store.SetSessionStateAsync("q", "old-state", new byte[] { 1 });
        var cutoff = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.SetSessionStateAsync("q", "fresh-state", new byte[] { 2 });
        await store.EnqueueAsync("q", [3], sessionId: "messages-only");

        // Act
        var filtered = store.ListSessions("q", stateUpdatedAfter: cutoff);
        var unfiltered = store.ListSessions("q");

        // Assert
        filtered.ShouldBe(new[] { "fresh-state" }, "only state updated strictly after the cutoff matches");
        unfiltered.ShouldBe(new[] { "fresh-state", "messages-only", "old-state" });
    }

    [Fact]
    public async Task ListSessions_FilterMode_ExcludesClearedState_AndReSetStateMatchesAgain()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var store = new InMemoryMessageStore(clock);
        await store.CreateQueueAsync("q");
        var cutoff = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetSessionStateAsync("q", "s-1", new byte[] { 1 });

        // Act + Assert
        store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBe(new[] { "s-1" });

        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetSessionStateAsync("q", "s-1", null);
        store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBeEmpty("cleared state never matches filter mode");
        store.ListSessions("q").ShouldBeEmpty("cleared state does not count as stored state");

        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetSessionStateAsync("q", "s-1", new byte[] { 2 });
        store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBe(new[] { "s-1" });
    }
}
