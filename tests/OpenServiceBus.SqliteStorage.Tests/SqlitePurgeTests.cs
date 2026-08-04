using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.SqliteStorage;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// Purge parity with the in-memory store (issue #36): messages, locks, session state and
/// dedup history go; the queue and live session locks stay. See docs/Purge.md.
/// </summary>
public class SqlitePurgeTests
{
    private static SqliteMessageStore NewStore() =>
        new(new SqliteStorageOptions { DataSource = ":memory:" },
            TimeProvider.System,
            NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task Purge_RemovesActiveScheduledAndLockedMessages_AndStaysUsable()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 });
        await store.EnqueueAsync("q", new byte[] { 2 }, scheduledEnqueueTime: DateTimeOffset.UtcNow.AddHours(1));
        var held = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));

        var purged = await store.PurgeAsync("q");

        purged.ShouldBe(2L);
        (await store.CountAsync("q")).ShouldBe(0L);
        (await store.TryCompleteAsync("q", held!.LockToken)).ShouldBeFalse();

        await store.EnqueueAsync("q", new byte[] { 3 });
        var relocked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        relocked.ShouldNotBeNull();
        (await store.TryCompleteAsync("q", relocked.LockToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Purge_ClearsSessionStateAndDedupHistory_ButKeepsTheSessionLock()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        var window = TimeSpan.FromMinutes(10);
        var original = await store.EnqueueAsync("q", new byte[] { 1 }, sessionId: "s-1", messageId: "m-1", duplicateDetectionWindow: window);
        (await store.TryAcceptSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-a")).ShouldNotBeNull();
        await store.SetSessionStateAsync("q", "s-1", new byte[] { 0xAA });

        await store.PurgeAsync("q");

        (await store.GetSessionStateAsync("q", "s-1")).ShouldBeNull();
        (await store.TryAcceptSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-b")).ShouldBeNull(
            "the purge must not steal a session lock a live receiver is holding");
        var resent = await store.EnqueueAsync("q", new byte[] { 2 }, sessionId: "s-1", messageId: "m-1", duplicateDetectionWindow: window);
        resent.SequenceNumber.ShouldNotBe(original.SequenceNumber);
        (await store.CountAsync("q")).ShouldBe(1L);
    }

    [Fact]
    public async Task Purge_UnknownQueue_ReturnsZeroWithoutThrowing()
    {
        await using var store = NewStore();
        (await store.PurgeAsync("nope")).ShouldBe(0L);
    }
}
