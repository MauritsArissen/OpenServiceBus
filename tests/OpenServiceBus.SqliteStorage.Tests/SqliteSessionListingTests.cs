using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.SqliteStorage.Tests;

public class SqliteSessionListingTests
{
    private static SqliteMessageStore NewStore(TimeProvider? tp = null) =>
        new(new SqliteStorageOptions { DataSource = ":memory:" },
            tp ?? TimeProvider.System,
            NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task ListSessions_ReturnsSessionsWithAvailableMessagesOrState()
    {
        // Arrange
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 }, sessionId: "with-messages");
        await store.SetSessionStateAsync("q", "with-state-only", new byte[] { 9 });

        // Act
        var ids = store.ListSessions("q");

        // Assert
        ids.ShouldBe(new[] { "with-messages", "with-state-only" });
    }

    [Fact]
    public async Task ListSessions_OrdersBySessionIdOrdinal_AndPagesWithSkipAndTop()
    {
        // Arrange
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        foreach (var id in new[] { "s-3", "s-1", "s-5", "s-2", "s-4" })
        {
            await store.EnqueueAsync("q", new byte[] { 1 }, sessionId: id);
        }

        // Act + Assert
        store.ListSessions("q").ShouldBe(new[] { "s-1", "s-2", "s-3", "s-4", "s-5" });
        store.ListSessions("q", skip: 0, top: 2).ShouldBe(new[] { "s-1", "s-2" });
        store.ListSessions("q", skip: 2, top: 2).ShouldBe(new[] { "s-3", "s-4" });
        store.ListSessions("q", skip: 4, top: 2).ShouldBe(new[] { "s-5" });
        store.ListSessions("q", skip: 5, top: 2).ShouldBeEmpty();
        store.ListSessions("q", top: 0).ShouldBeEmpty();
        store.ListSessions("q", skip: -3, top: 1).ShouldBe(new[] { "s-1" });
    }

    [Fact]
    public async Task ListSessions_ExcludesDeferredScheduledAndLockedMessages()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var store = NewStore(clock);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 }, sessionId: "deferred");
        await store.EnqueueAsync("q", new byte[] { 2 }, sessionId: "scheduled",
            scheduledEnqueueTime: clock.GetUtcNow().AddMinutes(10));
        await store.EnqueueAsync("q", new byte[] { 3 }, sessionId: "locked");
        await store.EnqueueAsync("q", new byte[] { 4 }, sessionId: "active");

        await store.TryAcceptSessionAsync("q", "deferred", TimeSpan.FromMinutes(1));
        var toDefer = await store.TryDequeueFromSessionAsync("q", "deferred", TimeSpan.FromMinutes(1));
        await store.TryDeferAsync("q", toDefer!.LockToken);
        await store.ReleaseSessionAsync("q", "deferred");

        await store.TryAcceptSessionAsync("q", "locked", TimeSpan.FromMinutes(1));
        var held = await store.TryDequeueFromSessionAsync("q", "locked", TimeSpan.FromMinutes(1));

        // Act + Assert
        store.ListSessions("q").ShouldBe(new[] { "active" },
            "deferred, future-scheduled and locked messages do not make a session listable");

        await store.TryAbandonAsync("q", held!.LockToken);
        store.ListSessions("q").ShouldBe(new[] { "active", "locked" }, "abandon makes the message available again");
    }

    [Fact]
    public async Task ListSessions_WithStateUpdatedAfter_FiltersOnStateUpdateInstant()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var store = NewStore(clock);
        await store.CreateQueueAsync("q");
        await store.SetSessionStateAsync("q", "old-state", new byte[] { 1 });
        var cutoff = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.SetSessionStateAsync("q", "fresh-state", new byte[] { 2 });
        await store.EnqueueAsync("q", new byte[] { 3 }, sessionId: "messages-only");

        // Act + Assert
        store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBe(new[] { "fresh-state" });
        store.ListSessions("q").ShouldBe(new[] { "fresh-state", "messages-only", "old-state" });
    }

    [Fact]
    public async Task ListSessions_FilterMode_ExcludesClearedState()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var store = NewStore(clock);
        await store.CreateQueueAsync("q");
        var cutoff = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetSessionStateAsync("q", "s-1", new byte[] { 1 });
        store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBe(new[] { "s-1" });

        // Act
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetSessionStateAsync("q", "s-1", null);

        // Assert
        store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBeEmpty();
        store.ListSessions("q").ShouldBeEmpty();
    }

    [Fact]
    public async Task ListSessions_DatabaseFromBeforeUpdatedAtColumn_MigratesAndKeepsOldRows()
    {
        // Arrange - fabricate a database whose session_state table predates updated_at.
        var path = Path.Combine(Path.GetTempPath(), $"osb-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var seed = new SqliteConnection($"Data Source={path}"))
            {
                seed.Open();
                using var ddl = seed.CreateCommand();
                ddl.CommandText = """
                    CREATE TABLE queues (name TEXT PRIMARY KEY COLLATE NOCASE);
                    CREATE TABLE session_state (
                        queue_name  TEXT NOT NULL COLLATE NOCASE,
                        session_id  TEXT NOT NULL,
                        state       BLOB NULL,
                        PRIMARY KEY (queue_name, session_id),
                        FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
                    );
                    INSERT INTO queues(name) VALUES ('q');
                    INSERT INTO session_state(queue_name, session_id, state) VALUES ('q', 'legacy', x'01');
                    """;
                ddl.ExecuteNonQuery();
            }

            var clock = new FakeTimeProvider();
            await using var store = new SqliteMessageStore(
                new SqliteStorageOptions { DataSource = path },
                clock,
                NullLogger<SqliteMessageStore>.Instance);
            var cutoff = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromMinutes(1));
            await store.SetSessionStateAsync("q", "fresh", new byte[] { 2 });

            // Act + Assert
            store.ListSessions("q").ShouldBe(new[] { "fresh", "legacy" },
                "pre-migration rows still count as stored state in default mode");
            store.ListSessions("q", stateUpdatedAfter: cutoff).ShouldBe(new[] { "fresh" },
                "pre-migration rows have no update instant and never match filter mode");
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch { }
        }
    }
}
