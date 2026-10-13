using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class CountScheduledTests
{
    [Fact]
    public async Task CountScheduled_MixOfActiveAndScheduled_CountsOnlyScheduled()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1]);
        await store.EnqueueAsync("q", [2], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromMinutes(5));
        await store.EnqueueAsync("q", [3], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromMinutes(10));

        // Act + Assert
        store.CountScheduled("q").ShouldBe(2L);
    }

    [Fact]
    public async Task CountScheduled_AfterActivation_DropsToZero()
    {
        // Arrange
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(10));

        // Act
        store.ActivateScheduled("q", time.GetUtcNow());

        // Assert
        store.CountScheduled("q").ShouldBe(0L);
        (await store.CountAsync("q")).ShouldBe(1L, "the message is still stored, just no longer scheduled");
    }

    [Fact]
    public void CountScheduled_UnknownQueue_ReturnsZero()
    {
        var store = new InMemoryMessageStore(TimeProvider.System);

        store.CountScheduled("nope").ShouldBe(0L);
    }
}
