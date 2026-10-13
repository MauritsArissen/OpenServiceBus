using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Transactions;
using OpenServiceBus.InMemoryStorage.Transactions;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// The transaction manager's two-phase commit: every operation's Validate runs before any
/// Apply, so a precondition failure rejects the discharge with no partial effects.
/// </summary>
public class TransactionManagerTests
{
    private static TransactionManager NewManager() => new(NullLogger<TransactionManager>.Instance);

    private static TransactionalOperation Op(
        string entity,
        Func<CancellationToken, Task> apply,
        Func<CancellationToken, Task>? validate = null) =>
        new() { Entity = entity, Apply = apply, Validate = validate };

    [Fact]
    public async Task CommitAsync_AppliesOperationsInEnlistOrder()
    {
        var manager = NewManager();
        var txnId = manager.Declare();
        var applied = new List<int>();
        manager.Enlist(txnId, Op("q", _ => { applied.Add(1); return Task.CompletedTask; })).ShouldBeTrue();
        manager.Enlist(txnId, Op("q", _ => { applied.Add(2); return Task.CompletedTask; })).ShouldBeTrue();

        await manager.CommitAsync(txnId);

        applied.ShouldBe(new[] { 1, 2 });
        manager.OpenCount.ShouldBe(0);
    }

    [Fact]
    public void Enlist_UnknownTxnId_ReturnsFalse()
    {
        var manager = NewManager();
        manager.Enlist(new byte[] { 1, 2, 3 }, Op("q", _ => Task.CompletedTask)).ShouldBeFalse();
    }

    [Fact]
    public async Task Rollback_DiscardsBufferedOperations()
    {
        var manager = NewManager();
        var txnId = manager.Declare();
        var applied = false;
        manager.Enlist(txnId, Op("q", _ => { applied = true; return Task.CompletedTask; })).ShouldBeTrue();

        manager.Rollback(txnId);
        await manager.CommitAsync(txnId);

        applied.ShouldBeFalse();
        manager.OpenCount.ShouldBe(0);
        manager.Enlist(txnId, Op("q", _ => Task.CompletedTask)).ShouldBeFalse();
    }

    [Fact]
    public async Task CommitAsync_ValidationFailure_AppliesNothingAndThrows()
    {
        // A failing Validate on the SECOND op must keep the FIRST op from applying too:
        // validation of every op runs before any apply.
        var manager = NewManager();
        var txnId = manager.Declare();
        var applied = new List<int>();
        manager.Enlist(txnId, Op("q1", _ => { applied.Add(1); return Task.CompletedTask; })).ShouldBeTrue();
        manager.Enlist(txnId, Op("q2",
            _ => { applied.Add(2); return Task.CompletedTask; },
            validate: _ => throw new InvalidOperationException("lock lost"))).ShouldBeTrue();

        await Should.ThrowAsync<InvalidOperationException>(() => manager.CommitAsync(txnId));

        applied.ShouldBeEmpty();
        manager.OpenCount.ShouldBe(0);
    }

    [Fact]
    public async Task CommitAsync_ValidationFailure_LeavesStoreUntouched()
    {
        // Same scenario against the real store: a buffered enqueue followed by an op whose
        // validation fails must leave the queue empty after the rejected commit.
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        var manager = NewManager();
        var txnId = manager.Declare();
        manager.Enlist(txnId, Op("q", _ => store.EnqueueAsync("q", new byte[] { 1 }))).ShouldBeTrue();
        manager.Enlist(txnId, Op("q",
            _ => Task.CompletedTask,
            validate: _ => throw new InvalidOperationException("entity deleted"))).ShouldBeTrue();

        await Should.ThrowAsync<InvalidOperationException>(() => manager.CommitAsync(txnId));

        (await store.CountAsync("q")).ShouldBe(0L);
    }

    [Fact]
    public async Task CommitAsync_AllValidationsRunBeforeAnyApply()
    {
        var manager = NewManager();
        var txnId = manager.Declare();
        var order = new List<string>();
        manager.Enlist(txnId, Op("q1",
            _ => { order.Add("apply1"); return Task.CompletedTask; },
            validate: _ => { order.Add("validate1"); return Task.CompletedTask; })).ShouldBeTrue();
        manager.Enlist(txnId, Op("q2",
            _ => { order.Add("apply2"); return Task.CompletedTask; },
            validate: _ => { order.Add("validate2"); return Task.CompletedTask; })).ShouldBeTrue();

        await manager.CommitAsync(txnId);

        order.ShouldBe(new[] { "validate1", "validate2", "apply1", "apply2" });
    }

    [Fact]
    public async Task CommitAsync_ApplyFault_StopsAndRethrows()
    {
        // A genuine store fault in the apply phase (nothing validation could have caught)
        // still fails the discharge; ops after the faulting one are not attempted.
        var manager = NewManager();
        var txnId = manager.Declare();
        var applied = new List<int>();
        manager.Enlist(txnId, Op("q1", _ => { applied.Add(1); return Task.CompletedTask; })).ShouldBeTrue();
        manager.Enlist(txnId, Op("q2", _ => throw new IOException("store fault"))).ShouldBeTrue();
        manager.Enlist(txnId, Op("q3", _ => { applied.Add(3); return Task.CompletedTask; })).ShouldBeTrue();

        await Should.ThrowAsync<IOException>(() => manager.CommitAsync(txnId));

        applied.ShouldBe(new[] { 1 });
        manager.OpenCount.ShouldBe(0);
    }

    [Fact]
    public async Task CommitAsync_CrossEntityOperations_CommitTogether()
    {
        // One txn spanning two entities: the grouped commit lands both.
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", new byte[] { 1 });
        var locked = await store.TryDequeueAsync("a", TimeSpan.FromSeconds(30));
        locked.ShouldNotBeNull();

        var manager = NewManager();
        var txnId = manager.Declare();
        manager.Enlist(txnId, Op("b", _ => store.EnqueueAsync("b", new byte[] { 2 }))).ShouldBeTrue();
        manager.Enlist(txnId, Op("a", async _ =>
        {
            (await store.TryCompleteAsync("a", locked.LockToken)).ShouldBeTrue();
        })).ShouldBeTrue();

        await manager.CommitAsync(txnId);

        (await store.CountAsync("a")).ShouldBe(0L);
        (await store.CountAsync("b")).ShouldBe(1L);
    }

    [Fact]
    public async Task CommitAsync_StaleLockDetectedInValidation_NoPartialSend()
    {
        // The real cross-entity failure shape: a buffered send to B plus a buffered
        // complete on A whose lock expired before the discharge. Validation (lock no
        // longer held) must reject the commit BEFORE the send to B applies.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", new byte[] { 1 });
        var locked = await store.TryDequeueAsync("a", TimeSpan.FromSeconds(5));
        locked.ShouldNotBeNull();

        var manager = NewManager();
        var txnId = manager.Declare();
        manager.Enlist(txnId, Op("b", _ => store.EnqueueAsync("b", new byte[] { 2 }))).ShouldBeTrue();
        manager.Enlist(txnId, Op("a",
            async _ =>
            {
                var settled = await store.TryCompleteAsync("a", locked.LockToken);
                if (!settled) throw new InvalidOperationException("lock lost");
            },
            validate: async _ =>
            {
                var held = await store.TryGetLockedAsync("a", locked.LockToken);
                if (held is null) throw new InvalidOperationException("lock lost");
            })).ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(10));

        await Should.ThrowAsync<InvalidOperationException>(() => manager.CommitAsync(txnId));

        (await store.CountAsync("b")).ShouldBe(0L, "the buffered send must not apply when a sibling op fails validation");
        (await store.CountAsync("a")).ShouldBe(1L);
    }
}
