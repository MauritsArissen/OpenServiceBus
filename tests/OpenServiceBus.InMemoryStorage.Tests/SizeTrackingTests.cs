using OpenServiceBus.InMemoryStorage;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class SizeTrackingTests
{
    [Fact]
    public async Task GetSizeInBytes_TracksEnqueueCompleteAndExpiry()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        store.GetSizeInBytes("q").ShouldBe(0L);

        await store.EnqueueAsync("q", new byte[100]);
        await store.EnqueueAsync("q", new byte[250]);
        store.GetSizeInBytes("q").ShouldBe(350L);

        var locked = await store.TryDequeueAsync("q", TimeSpan.FromMinutes(1));
        store.GetSizeInBytes("q").ShouldBe(350L, "locked messages still occupy space");
        await store.TryCompleteAsync("q", locked!.LockToken);
        store.GetSizeInBytes("q").ShouldBe(250L);

        var expiring = await store.EnqueueAsync("q", new byte[50], expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-1));
        expiring.ShouldNotBeNull();
        store.ExpireMessages("q", DateTimeOffset.UtcNow);
        store.GetSizeInBytes("q").ShouldBe(250L);
    }

    [Fact]
    public void GetSizeInBytes_UnknownQueue_ReturnsZero()
    {
        new InMemoryMessageStore().GetSizeInBytes("ghost").ShouldBe(0L);
    }
}
