using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// Idle long-poll behavior (issue #90, receive path): a parked TryDequeueAsync on a queue the
/// store knows is empty must not run claim transactions, yet every transition that makes a
/// message available (enqueue, lock expiry, scheduled activation) must wake it promptly, and
/// rows written by another connection on a shared db file must surface via the safety probe.
/// See docs/Persistence.md.
/// </summary>
public class SqliteIdleDequeueTests
{
    private static SqliteMessageStore NewStore(TimeProvider? time = null, string dataSource = ":memory:") =>
        new(new SqliteStorageOptions { DataSource = dataSource },
            time ?? TimeProvider.System,
            NullLogger<SqliteMessageStore>.Instance);

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"osb-idle-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task ManyParkedWaiters_EmptyQueue_StayOffTheStore()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        using var cts = new CancellationTokenSource();
        var waiters = Enumerable.Range(0, 50)
            .Select(_ => store.TryDequeueAsync("q", TimeSpan.FromSeconds(30), cancellationToken: cts.Token))
            .ToList();
        await Task.Delay(500);

        var before = store.ClaimAttempts;
        await Task.Delay(2000);
        var parkedClaims = store.ClaimAttempts - before;

        parkedClaims.ShouldBeLessThanOrEqualTo(4,
            "50 parked waiters on an empty queue must cost ~1 safety probe per second in total, not ~4 claims per second each");
        cts.Cancel();
        (await Task.WhenAll(waiters)).ShouldAllBe(m => m == null);
    }

    [Fact]
    public async Task ManyParkedSessionWaiters_EmptySessionfulQueue_StayOffTheStore()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        using var cts = new CancellationTokenSource();
        var waiters = new List<Task<Core.Messaging.LockedMessage?>>();
        for (var i = 0; i < 20; i++)
        {
            (await store.TryAcceptSessionAsync("q", $"s-{i}", TimeSpan.FromMinutes(5), $"link-{i}")).ShouldNotBeNull();
            waiters.Add(store.TryDequeueFromSessionAsync("q", $"s-{i}", TimeSpan.FromSeconds(30), $"link-{i}", cts.Token));
        }
        await Task.Delay(500);

        var before = store.ClaimAttempts;
        await Task.Delay(2000);
        var parkedClaims = store.ClaimAttempts - before;

        parkedClaims.ShouldBeLessThanOrEqualTo(4,
            "a session-filtered miss must still let an empty sessionful queue park its receivers");
        cts.Cancel();
        (await Task.WhenAll(waiters)).ShouldAllBe(m => m == null);
    }

    [Fact]
    public async Task ParkedWaiter_EnqueueOnKnownEmptyQueue_WakesAndDelivers()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = store.TryDequeueAsync("q", TimeSpan.FromSeconds(30), cancellationToken: cts.Token);
        await Task.Delay(600);
        waiter.IsCompleted.ShouldBeFalse("nothing is available yet, the waiter must stay parked");

        await store.EnqueueAsync("q", [1]);

        var locked = await waiter;
        locked.ShouldNotBeNull();
        locked.Message.EncodedMessage.ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task ParkedWaiter_LockExpiry_WakesAndRedelivers()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var store = NewStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1]);
        var held = await store.TryDequeueAsync("q", TimeSpan.FromSeconds(10));
        held.ShouldNotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = store.TryDequeueAsync("q", TimeSpan.FromSeconds(10), cancellationToken: cts.Token);
        await Task.Delay(600);
        waiter.IsCompleted.ShouldBeFalse("the only message is locked, the waiter must stay parked");

        time.Advance(TimeSpan.FromSeconds(30));
        store.ExpireLocksAll(time.GetUtcNow()).ByQueue["q"].ShouldBe(1);

        var redelivered = await waiter;
        redelivered.ShouldNotBeNull();
        redelivered.Message.DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task ParkedWaiter_ScheduledActivation_WakesAndDelivers()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var store = NewStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = store.TryDequeueAsync("q", TimeSpan.FromSeconds(30), cancellationToken: cts.Token);
        await Task.Delay(600);
        waiter.IsCompleted.ShouldBeFalse("the only message is still scheduled, the waiter must stay parked");

        time.Advance(TimeSpan.FromMinutes(1));
        store.ActivateScheduledAll(time.GetUtcNow()).ByQueue["q"].ShouldBe(1);

        var activated = await waiter;
        activated.ShouldNotBeNull();
        activated.Message.EncodedMessage.ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task ParkedSessionWaiter_SessionMessageArrives_WakesAndDelivers()
    {
        await using var store = NewStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], sessionId: "s-1");
        (await store.TryAcceptSessionAsync("q", "s-1", TimeSpan.FromMinutes(5), "link-a")).ShouldNotBeNull();
        var first = await store.TryDequeueFromSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-a");
        first.ShouldNotBeNull();
        (await store.TryCompleteAsync("q", first.LockToken)).ShouldBeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = store.TryDequeueFromSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link-a", cts.Token);
        await Task.Delay(600);
        waiter.IsCompleted.ShouldBeFalse("the session is drained, the waiter must stay parked");

        await store.EnqueueAsync("q", [2], sessionId: "s-1");

        var next = await waiter;
        next.ShouldNotBeNull();
        next.Message.EncodedMessage.ShouldBe(new byte[] { 2 });
    }

    [Fact]
    public async Task ParkedWaiter_RowWrittenByAnotherConnection_SurfacesViaSafetyProbe()
    {
        var path = TempDb();
        try
        {
            await using var storeA = NewStore(dataSource: path);
            await storeA.CreateQueueAsync("q");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var waiter = storeA.TryDequeueAsync("q", TimeSpan.FromSeconds(30), cancellationToken: cts.Token);
            await Task.Delay(600);
            waiter.IsCompleted.ShouldBeFalse();

            await using (var storeB = NewStore(dataSource: path))
            {
                await storeB.EnqueueAsync("q", [7]);
            }

            var locked = await waiter;
            locked.ShouldNotBeNull("the once-per-second safety probe must find rows no in-process notify announced");
            locked.Message.EncodedMessage.ShouldBe(new byte[] { 7 });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
