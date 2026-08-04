using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// Descriptor snapshots survive a store restart, which is what lets the Host's
/// rehydration bring queues back with their real settings (status included) instead of
/// defaults. Uses a shared on-disk file per test to simulate the restart.
/// </summary>
public class SqliteDescriptorPersistenceTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"osb-desc-{Guid.NewGuid():N}.db");

    private static SqliteMessageStore Open(string path) =>
        new(new SqliteStorageOptions { DataSource = path }, TimeProvider.System, NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task SaveQueueDescriptor_SurvivesReopeningTheStore()
    {
        var path = TempDb();
        try
        {
            var descriptor = new QueueDescriptor { Name = "frozen", Status = EntityStatus.SendDisabled, MaxDeliveryCount = 3 };
            await using (var store = Open(path))
            {
                await store.CreateQueueAsync("frozen");
                await store.SaveQueueDescriptorAsync("frozen", QueueDescriptorJson.Serialize(descriptor));
            }

            await using var reopened = Open(path);
            var loaded = reopened.LoadQueueDescriptors();
            loaded.ContainsKey("frozen").ShouldBeTrue();
            var restored = QueueDescriptorJson.Deserialize(loaded["frozen"]);
            restored.ShouldNotBeNull();
            restored.Status.ShouldBe(EntityStatus.SendDisabled);
            restored.MaxDeliveryCount.ShouldBe(3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveQueueDescriptor_SecondSaveReplacesTheFirst()
    {
        var path = TempDb();
        try
        {
            await using var store = Open(path);
            await store.CreateQueueAsync("q");
            await store.SaveQueueDescriptorAsync("q", QueueDescriptorJson.Serialize(new QueueDescriptor { Name = "q" }));
            await store.SaveQueueDescriptorAsync("q",
                QueueDescriptorJson.Serialize(new QueueDescriptor { Name = "q", Status = EntityStatus.Disabled }));

            var restored = QueueDescriptorJson.Deserialize(store.LoadQueueDescriptors()["q"]);
            restored!.Status.ShouldBe(EntityStatus.Disabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteQueue_AlsoRemovesTheDescriptorSnapshot()
    {
        var path = TempDb();
        try
        {
            await using var store = Open(path);
            await store.CreateQueueAsync("gone");
            await store.SaveQueueDescriptorAsync("gone", QueueDescriptorJson.Serialize(new QueueDescriptor { Name = "gone" }));

            await store.DeleteQueueAsync("gone");

            store.LoadQueueDescriptors().ContainsKey("gone").ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
