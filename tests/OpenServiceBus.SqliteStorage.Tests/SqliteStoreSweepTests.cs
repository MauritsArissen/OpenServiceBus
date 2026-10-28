using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// Store-wide sweep parity with the in-memory store (issue #90): one store call per sweeper
/// tick instead of one per queue, with a HasPending signal the background sweepers use to
/// back off while idle. See docs/Background-Sweepers.md.
/// </summary>
public class SqliteStoreSweepTests
{
    private static (SqliteMessageStore Store, FakeTimeProvider Time) NewStore()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new SqliteMessageStore(
            new SqliteStorageOptions { DataSource = ":memory:" },
            time,
            NullLogger<SqliteMessageStore>.Instance);
        return (store, time);
    }

    [Fact]
    public async Task ExpireLocksAll_ReleasesExpiredLocksAcrossQueues_AndReportsPerQueueCounts()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", [1]);
        await store.EnqueueAsync("a", [2]);
        await store.EnqueueAsync("b", [3]);
        await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10));
        await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10));
        await store.TryDequeueAsync("b", TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ExpireLocksAll(time.GetUtcNow());

        sweep.ByQueue["a"].ShouldBe(2);
        sweep.ByQueue["b"].ShouldBe(1);
        sweep.HasPending.ShouldBeFalse("every lock was released, nothing is left to expire");
        var redelivered = await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10));
        redelivered.ShouldNotBeNull("released messages must be available for redelivery");
        redelivered.Message.DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExpireLocksAll_UnexpiredLocksRemain_ReportsPending()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);
        await store.TryDequeueAsync("a", TimeSpan.FromMinutes(5));

        var sweep = store.ExpireLocksAll(time.GetUtcNow());

        sweep.ByQueue.ShouldBeEmpty();
        sweep.HasPending.ShouldBeTrue("a live lock still needs future sweeps");
    }

    [Fact]
    public async Task ExpireLocksAll_ExpiredDeferredOriginLock_RestoresDeferredState()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);
        var locked = await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10));
        await store.TryDeferAsync("a", locked!.LockToken);
        var redeferred = await store.TryReceiveDeferredAsync("a", locked.Message.SequenceNumber, TimeSpan.FromSeconds(10));
        redeferred.ShouldNotBeNull();
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ExpireLocksAll(time.GetUtcNow());

        sweep.ByQueue["a"].ShouldBe(1);
        (await store.TryReceiveDeferredAsync("a", locked.Message.SequenceNumber, TimeSpan.FromSeconds(10))).ShouldNotBeNull(
            "an expired lock on a deferred-origin message must return it to Deferred, not Active");
    }

    [Fact]
    public async Task ExpireMessagesAll_RemovesExpiredAcrossQueues_SkipsLockedAndFutureTtl()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        var soon = time.GetUtcNow() + TimeSpan.FromSeconds(10);
        var later = time.GetUtcNow() + TimeSpan.FromMinutes(5);
        await store.EnqueueAsync("a", [1], expiresAt: soon);
        await store.EnqueueAsync("a", [2], expiresAt: later);
        await store.EnqueueAsync("b", [3], expiresAt: soon);
        await store.EnqueueAsync("b", [4], expiresAt: soon);
        var held = await store.TryDequeueAsync("b", TimeSpan.FromMinutes(5));
        held.ShouldNotBeNull();
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ExpireMessagesAll(time.GetUtcNow());

        sweep.ByQueue["a"].Count.ShouldBe(1);
        sweep.ByQueue["a"][0].EncodedMessage.ShouldBe(new byte[] { 1 });
        sweep.ByQueue["b"].Count.ShouldBe(1);
        sweep.ByQueue["b"][0].EncodedMessage.ShouldBe(new byte[] { 4 });
        sweep.HasPending.ShouldBeTrue("the locked expired message and the future-TTL message remain");
        (await store.CountAsync("a")).ShouldBe(1L);
        (await store.CountAsync("b")).ShouldBe(1L);
    }

    [Fact]
    public async Task ExpireMessagesAll_OnlyMessagesWithoutTtl_ReportsNothingPending()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);

        var sweep = store.ExpireMessagesAll(time.GetUtcNow());

        sweep.ByQueue.ShouldBeEmpty();
        sweep.HasPending.ShouldBeFalse("no stored message carries a TTL");
    }

    [Fact]
    public async Task ActivateScheduledAll_ActivatesDueMessagesAcrossQueues_AndReportsPending()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", [1], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        await store.EnqueueAsync("a", [2], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromMinutes(10));
        await store.EnqueueAsync("b", [3], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ActivateScheduledAll(time.GetUtcNow());

        sweep.ByQueue["a"].ShouldBe(1);
        sweep.ByQueue["b"].ShouldBe(1);
        sweep.HasPending.ShouldBeTrue("one message is still scheduled for later");
        (await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10)))!.Message.EncodedMessage.ShouldBe(new byte[] { 1 });
        (await store.TryDequeueAsync("b", TimeSpan.FromSeconds(10)))!.Message.EncodedMessage.ShouldBe(new byte[] { 3 });
    }

    [Fact]
    public async Task ActivateScheduledAll_NothingScheduled_ReportsNothingPending()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);

        var sweep = store.ActivateScheduledAll(time.GetUtcNow());

        sweep.ByQueue.ShouldBeEmpty();
        sweep.HasPending.ShouldBeFalse();
    }

    [Fact]
    public async Task PurgeAllAsync_ClearsEveryQueueAndBothDedupHistories_ButKeepsSessionLocks()
    {
        var (store, time) = NewStore();
        await using var _ = store;
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        var window = TimeSpan.FromMinutes(10);
        await store.EnqueueAsync("a", [1], messageId: "m-1", duplicateDetectionWindow: window);
        await store.EnqueueAsync("a", [2], sessionId: "s-1");
        await store.EnqueueAsync("b", [3]);
        var sessionLock = await store.TryAcceptSessionAsync("a", "s-1", TimeSpan.FromMinutes(5), "link-a");
        sessionLock.ShouldNotBeNull();
        await store.SetSessionStateAsync("a", "s-1", [0xAA]);
        await store.TryDequeueAsync("b", TimeSpan.FromMinutes(5));
        (await store.CheckTopicDuplicateAsync("t", "m-1", window)).ShouldBeFalse();

        var purged = await store.PurgeAllAsync();

        purged.ShouldBe(3L);
        (await store.CountAsync("a")).ShouldBe(0L);
        (await store.CountAsync("b")).ShouldBe(0L);
        (await store.GetSessionStateAsync("a", "s-1")).ShouldBeNull();
        (await store.CheckTopicDuplicateAsync("t", "m-1", window)).ShouldBeFalse(
            "a broker-wide purge must also forget topic duplicate-detection history");
        var resent = await store.EnqueueAsync("a", [4], messageId: "m-1", duplicateDetectionWindow: window);
        resent.EncodedMessage.ShouldBe(new byte[] { 4 },
            "queue dedup history must be gone, so the same MessageId lands as a fresh message");
        (await store.TryAcceptSessionAsync("a", "s-1", TimeSpan.FromMinutes(5), "link-b")).ShouldBeNull(
            "the purge must not steal a session lock a live receiver is holding");
    }
}
