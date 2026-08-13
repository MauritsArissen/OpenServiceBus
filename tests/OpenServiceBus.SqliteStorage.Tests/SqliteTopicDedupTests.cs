using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.SqliteStorage;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// SQLite topic duplicate detection and topic descriptor persistence (issue #29): the
/// dedup history and the descriptor snapshots live in their own tables and must survive a
/// broker restart - reopening the same database file restores both.
/// </summary>
public class SqliteTopicDedupTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static SqliteMessageStore NewStore(string dataSource, TimeProvider? tp = null) =>
        new(new SqliteStorageOptions { DataSource = dataSource },
            tp ?? TimeProvider.System,
            NullLogger<SqliteMessageStore>.Instance);

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"osb-topic-dedup-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task CheckTopicDuplicate_FirstSeenIsNotADuplicate_RepeatWithinWindowIs()
    {
        await using var store = NewStore(":memory:", new FakeTimeProvider());

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue();
    }

    [Fact]
    public async Task CheckTopicDuplicate_AfterTheWindowExpires_TheIdIsFreshAgain()
    {
        var clock = new FakeTimeProvider();
        await using var store = NewStore(":memory:", clock);

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
    }

    [Fact]
    public async Task CheckTopicDuplicate_ARepeatSlidesTheWindowForward()
    {
        var clock = new FakeTimeProvider();
        await using var store = NewStore(":memory:", clock);

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(8));
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(8));
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue(
            "the repeat at t+8 must have refreshed the window, so t+16 is still inside it");
    }

    [Fact]
    public async Task TopicDedupHistory_SurvivesARestart()
    {
        var path = TempDbPath();
        try
        {
            await using (var store = NewStore(path))
            {
                (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
            }

            await using var reopened = NewStore(path);
            (await reopened.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeTrue(
                "a duplicate sent right after a broker restart must still be dropped");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ClearTopicDedupHistory_ForgetsEverySeenId()
    {
        await using var store = NewStore(":memory:");

        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
        await store.ClearTopicDedupHistoryAsync("events");
        (await store.CheckTopicDuplicateAsync("events", "m-1", Window)).ShouldBeFalse();
    }

    [Fact]
    public async Task TopicDescriptors_RoundTripAndSurviveARestart()
    {
        var path = TempDbPath();
        try
        {
            var descriptor = new TopicDescriptor
            {
                Name = "events",
                RequiresDuplicateDetection = true,
                DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5),
                DefaultMessageTimeToLive = TimeSpan.FromHours(1),
            };
            await using (var store = NewStore(path))
            {
                await store.SaveTopicDescriptorAsync("events", TopicDescriptorJson.Serialize(descriptor));
            }

            await using var reopened = NewStore(path);
            var loaded = reopened.LoadTopicDescriptors();
            loaded.ContainsKey("events").ShouldBeTrue();
            var restored = TopicDescriptorJson.Deserialize(loaded["events"]);
            restored.ShouldNotBeNull();
            restored.RequiresDuplicateDetection.ShouldBeTrue();
            restored.DuplicateDetectionHistoryTimeWindow.ShouldBe(TimeSpan.FromMinutes(5));
            restored.DefaultMessageTimeToLive.ShouldBe(TimeSpan.FromHours(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteTopicDescriptor_RemovesTheSnapshot()
    {
        await using var store = NewStore(":memory:");
        await store.SaveTopicDescriptorAsync("events", TopicDescriptorJson.Serialize(new TopicDescriptor { Name = "events" }));

        await store.DeleteTopicDescriptorAsync("events");

        store.LoadTopicDescriptors().ContainsKey("events").ShouldBeFalse();
    }
}
