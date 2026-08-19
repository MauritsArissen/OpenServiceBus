using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// The publish-side sequence number (issue #55) in the persistent store: the
/// <c>enqueued_sequence_number</c> column round-trips through enqueue/dequeue/peek,
/// survives a restart, and is added to databases created before the column existed.
/// </summary>
public class SqliteEnqueuedSequenceNumberTests
{
    private static readonly byte[] Payload = [0x01];

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"osb-eseq-{Guid.NewGuid():N}.db");

    private static SqliteMessageStore Open(string path) =>
        new(new SqliteStorageOptions { DataSource = path }, TimeProvider.System, NullLogger<SqliteMessageStore>.Instance);

    private static SqliteMessageStore NewStore() => Open(":memory:");

    [Fact]
    public async Task Enqueue_FreshSend_EnqueuedSequenceNumberEqualsSequenceNumber()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");

        var first = await store.EnqueueAsync("q", Payload);
        var second = await store.EnqueueAsync("q", Payload);

        first.EnqueuedSequenceNumber.ShouldBe(first.SequenceNumber);
        second.EnqueuedSequenceNumber.ShouldBe(2L);
    }

    [Fact]
    public async Task Enqueue_ExplicitOriginalSequence_RoundTripsThroughDequeueAndPeek()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");

        var stored = await store.EnqueueAsync("q", Payload, enqueuedSequenceNumber: 42L);
        var peeked = store.Peek("q", 0, 10);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));

        stored.SequenceNumber.ShouldBe(1L);
        stored.EnqueuedSequenceNumber.ShouldBe(42L);
        peeked[0].EnqueuedSequenceNumber.ShouldBe(42L);
        locked!.Message.EnqueuedSequenceNumber.ShouldBe(42L);
    }

    [Fact]
    public async Task AllocateSequenceNumber_SharesTheCounterWithEnqueue()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");

        var allocated = await store.AllocateSequenceNumberAsync("q");
        var stored = await store.EnqueueAsync("q", Payload);

        allocated.ShouldBe(1L);
        stored.SequenceNumber.ShouldBe(2L);
    }

    [Fact]
    public async Task AllocateSequenceNumber_EntityWithoutAnyMessages_CreatesTheCounter()
    {
        await using var store = NewStore();

        var first = await store.AllocateSequenceNumberAsync("some-topic");
        var second = await store.AllocateSequenceNumberAsync("some-topic");

        first.ShouldBe(1L);
        second.ShouldBe(2L);
    }

    [Fact]
    public async Task Restart_PreservesTheOriginalEnqueuedSequenceNumber()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                await store.CreateQueueAsync("q");
                await store.EnqueueAsync("q", Payload, enqueuedSequenceNumber: 42L);
            }

            await using (var reopened = Open(path))
            {
                var locked = await reopened.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
                locked.ShouldNotBeNull();
                locked.Message.SequenceNumber.ShouldBe(1L);
                locked.Message.EnqueuedSequenceNumber.ShouldBe(42L);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OpeningAnOldDatabase_AddsTheColumn_AndOldRowsFallBackToTheirSequenceNumber()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                await store.CreateQueueAsync("q");
                await store.EnqueueAsync("q", Payload);
            }
            SqliteConnection.ClearAllPools();

            await using (var raw = new SqliteConnection($"Data Source={path}"))
            {
                raw.Open();
                using var drop = raw.CreateCommand();
                drop.CommandText = "ALTER TABLE messages DROP COLUMN enqueued_sequence_number";
                drop.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            await using (var reopened = Open(path))
            {
                var oldRow = reopened.Peek("q", 0, 10)[0];
                oldRow.EnqueuedSequenceNumber.ShouldBe(oldRow.SequenceNumber);

                var fresh = await reopened.EnqueueAsync("q", Payload, enqueuedSequenceNumber: 7L);
                fresh.EnqueuedSequenceNumber.ShouldBe(7L);
                reopened.Peek("q", fresh.SequenceNumber, 1)[0].EnqueuedSequenceNumber.ShouldBe(7L);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DeferredRetrieval_KeepsTheOriginalEnqueuedSequenceNumber()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", Payload, enqueuedSequenceNumber: 5L);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(30));
        await store.TryDeferAsync("q", locked!.LockToken);

        var deferred = await store.TryReceiveDeferredAsync("q", locked.Message.SequenceNumber, TimeSpan.FromSeconds(30));

        deferred.ShouldNotBeNull();
        deferred.Message.EnqueuedSequenceNumber.ShouldBe(5L);
    }
}
