using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.SqliteStorage;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// SQLite variants of the locked-payload accessors behind PropertiesToModify (issue #30),
/// including write-through so the swapped payload survives a restart.
/// </summary>
public class SqliteLockedPayloadTests
{
    private static SqliteMessageStore NewStore(string dataSource) =>
        new(new SqliteStorageOptions { DataSource = dataSource },
            TimeProvider.System,
            NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task TryGetLocked_ReturnsTheStoredMessage_OnlyWhileTheLockIsHeld()
    {
        await using var store = NewStore(":memory:");
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [0x01]);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromMinutes(1));
        locked.ShouldNotBeNull();

        var snapshot = await store.TryGetLockedAsync("q", locked.LockToken);
        snapshot.ShouldNotBeNull();
        snapshot.SequenceNumber.ShouldBe(locked.Message.SequenceNumber);
        snapshot.EncodedMessage.ShouldBe(new byte[] { 0x01 });

        await store.TryCompleteAsync("q", locked.LockToken);
        (await store.TryGetLockedAsync("q", locked.LockToken)).ShouldBeNull();
    }

    [Fact]
    public async Task TryUpdateLockedPayload_WritesThrough_AndSurvivesReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"osb-locked-payload-{Guid.NewGuid():N}.db");
        try
        {
            long seq;
            await using (var store = NewStore(path))
            {
                await store.CreateQueueAsync("q");
                await store.EnqueueAsync("q", [0x01]);
                var locked = await store.TryDequeueAsync("q", TimeSpan.FromMinutes(1));
                locked.ShouldNotBeNull();
                seq = locked.Message.SequenceNumber;
                (await store.TryUpdateLockedPayloadAsync("q", locked.LockToken, [0xAA, 0xBB])).ShouldBeTrue();
                await store.TryAbandonAsync("q", locked.LockToken);
            }

            await using var reopened = NewStore(path);
            var redelivered = await reopened.TryDequeueAsync("q", TimeSpan.FromMinutes(1));
            redelivered.ShouldNotBeNull();
            redelivered.Message.SequenceNumber.ShouldBe(seq);
            redelivered.Message.EncodedMessage.ShouldBe(new byte[] { 0xAA, 0xBB });
            redelivered.Message.DeliveryCount.ShouldBe(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryUpdateLockedPayload_UnknownToken_ReturnsFalse()
    {
        await using var store = NewStore(":memory:");
        await store.CreateQueueAsync("q");
        (await store.TryUpdateLockedPayloadAsync("q", Guid.NewGuid(), [0x01])).ShouldBeFalse();
    }
}
