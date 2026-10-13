using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// The scheduled-count query backing the management API's <c>ScheduledMessageCount</c>:
/// a SQL <c>COUNT</c> on the scheduled marker column, never a row materialization.
/// </summary>
public class SqliteCountScheduledTests
{
    private static SqliteMessageStore NewStore(TimeProvider? tp = null) =>
        new(new SqliteStorageOptions { DataSource = ":memory:" },
            tp ?? TimeProvider.System,
            NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task CountScheduled_MixOfActiveAndScheduled_CountsOnlyScheduled()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var store = NewStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 });
        await store.EnqueueAsync("q", new byte[] { 2 }, scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromMinutes(5));
        await store.EnqueueAsync("q", new byte[] { 3 }, scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromMinutes(10));

        // Act + Assert
        store.CountScheduled("q").ShouldBe(2L);
    }

    [Fact]
    public async Task CountScheduled_AfterActivation_DropsToZero()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var store = NewStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", new byte[] { 1 }, scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(10));

        // Act
        store.ActivateScheduled("q", time.GetUtcNow());

        // Assert
        store.CountScheduled("q").ShouldBe(0L);
        (await store.CountAsync("q")).ShouldBe(1L, "the message is still stored, just no longer scheduled");
    }

    [Fact]
    public async Task CountScheduled_UnknownQueue_ReturnsZero()
    {
        await using var store = NewStore();

        store.CountScheduled("nope").ShouldBe(0L);
    }
}
