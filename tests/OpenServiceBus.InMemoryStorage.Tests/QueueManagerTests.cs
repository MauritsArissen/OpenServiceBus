using OpenServiceBus.Core.Entities;
using OpenServiceBus.InMemoryStorage.Queues;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class QueueManagerTests
{
    [Fact]
    public async Task CreateAsync_NewQueue_PersistsDescriptorAndRaisesQueueCreatedForMainAndDlqSibling()
    {
        // Arrange
        var store = new InMemoryMessageStore();
        var manager = new QueueManager(store);
        var raised = new List<string>();
        manager.QueueCreated += (_, q) => raised.Add(q.Name);

        // Act
        var created = await manager.CreateAsync(new QueueDescriptor { Name = "orders", MaxDeliveryCount = 5 });
        var fetched = await manager.GetAsync("orders");

        // Assert
        created.MaxDeliveryCount.ShouldBe(5);
        fetched.ShouldBe(created);
        raised.ShouldContain("orders");
        raised.ShouldContain("orders/$DeadLetterQueue", "creating a main queue also raises QueueCreated for its DLQ sibling");
    }

    [Fact]
    public async Task CreateAsync_QueueAlreadyExists_ReturnsOriginalDescriptorWithoutOverwriting()
    {
        // Arrange
        var manager = new QueueManager(new InMemoryMessageStore());
        var original = await manager.CreateAsync(new QueueDescriptor { Name = "dup", MaxDeliveryCount = 3 });

        // Act
        var second = await manager.CreateAsync(new QueueDescriptor { Name = "dup", MaxDeliveryCount = 99 });

        // Assert
        second.MaxDeliveryCount.ShouldBe(3, "second create should not overwrite the first");
        second.ShouldBe(original);
    }

    [Fact]
    public async Task DeleteAsync_ExistingQueue_RaisesQueueDeletedForMainAndDlqSibling()
    {
        // Arrange
        var manager = new QueueManager(new InMemoryMessageStore());
        await manager.CreateAsync(new QueueDescriptor { Name = "gone" });
        var raised = new List<string>();
        manager.QueueDeleted += (_, q) => raised.Add(q.Name);

        // Act
        var firstDelete = await manager.DeleteAsync("gone");
        var secondDelete = await manager.DeleteAsync("gone");

        // Assert
        firstDelete.ShouldBeTrue();
        secondDelete.ShouldBeFalse();
        raised.ShouldContain("gone");
        raised.ShouldContain("gone/$DeadLetterQueue", "deleting a main queue cascades to its DLQ sibling");
    }

    [Fact]
    public async Task ListAsync_TwoMainQueuesCreated_ReturnsBothMainsAndTheirDlqSiblings()
    {
        // Arrange
        var manager = new QueueManager(new InMemoryMessageStore());
        await manager.CreateAsync(new QueueDescriptor { Name = "a" });
        await manager.CreateAsync(new QueueDescriptor { Name = "b" });

        // Act
        var all = await manager.ListAsync();

        // Assert
        all.Select(q => q.Name).OrderBy(n => n).ShouldBe(new[]
        {
            "a", "a/$DeadLetterQueue", "a/$Transfer/$DeadLetterQueue",
            "b", "b/$DeadLetterQueue", "b/$Transfer/$DeadLetterQueue",
        });
    }

    [Fact]
    public async Task CreateAsync_MainQueue_ImplicitlyCreatesDlqSiblingWithUnboundedDeliveryCount()
    {
        // Arrange
        var manager = new QueueManager(new InMemoryMessageStore());

        // Act
        await manager.CreateAsync(new QueueDescriptor { Name = "orders", MaxDeliveryCount = 3 });
        var dlq = await manager.GetAsync("orders/$DeadLetterQueue");

        // Assert
        dlq.ShouldNotBeNull();
        dlq!.MaxDeliveryCount.ShouldBe(int.MaxValue, "the DLQ should not itself dead-letter further");
    }

    [Fact]
    public async Task CreateAsync_DlqQueueDirectly_DoesNotRecurseToCreateDlqOfDlq()
    {
        // Arrange
        var manager = new QueueManager(new InMemoryMessageStore());

        // Act
        await manager.CreateAsync(new QueueDescriptor { Name = "raw/$DeadLetterQueue" });

        // Assert
        (await manager.GetAsync("raw/$DeadLetterQueue/$DeadLetterQueue")).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_MainQueue_CascadesToDlqSibling()
    {
        // Arrange
        var manager = new QueueManager(new InMemoryMessageStore());
        await manager.CreateAsync(new QueueDescriptor { Name = "ephemeral" });
        (await manager.GetAsync("ephemeral/$DeadLetterQueue")).ShouldNotBeNull();

        // Act
        var deleted = await manager.DeleteAsync("ephemeral");

        // Assert
        deleted.ShouldBeTrue();
        (await manager.GetAsync("ephemeral/$DeadLetterQueue")).ShouldBeNull();
    }
}
