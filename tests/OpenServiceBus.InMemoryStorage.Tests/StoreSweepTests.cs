using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// Store-wide sweep APIs (issue #90): one store call per sweeper tick instead of one per
/// queue, with a HasPending signal the background sweepers use to back off while idle.
/// See docs/Background-Sweepers.md.
/// </summary>
public class StoreSweepTests
{
    [Fact]
    public async Task ExpireLocksAll_ReleasesExpiredLocksAcrossQueues_AndReportsPerQueueCounts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", [1]);
        await store.EnqueueAsync("a", [2]);
        await store.EnqueueAsync("b", [3]);
        await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10));
        await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10));
        await store.TryDequeueAsync("b", TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ExpireLocksAll(time.GetUtcNow());

        sweep.ByQueue["a"].ShouldBe(2);
        sweep.ByQueue["b"].ShouldBe(1);
        sweep.HasPending.ShouldBeFalse("every lock was released, nothing is left to expire");
        (await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10))).ShouldNotBeNull(
            "released messages must be available for redelivery");
    }

    [Fact]
    public async Task ExpireLocksAll_UnexpiredLocksRemain_ReportsPending()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);
        await store.TryDequeueAsync("a", TimeSpan.FromMinutes(5));

        var sweep = store.ExpireLocksAll(time.GetUtcNow());

        sweep.ByQueue.ShouldBeEmpty();
        sweep.HasPending.ShouldBeTrue("a live lock still needs future sweeps");
    }

    [Fact]
    public async Task ExpireLocksAll_NoLocksAnywhere_ReportsNothingPending()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);

        var sweep = store.ExpireLocksAll(DateTimeOffset.UtcNow);

        sweep.ByQueue.ShouldBeEmpty();
        sweep.HasPending.ShouldBeFalse();
    }

    [Fact]
    public async Task ExpireMessagesAll_RemovesExpiredAcrossQueues_SkipsLockedAndFutureTtl()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        var soon = time.GetUtcNow() + TimeSpan.FromSeconds(10);
        var later = time.GetUtcNow() + TimeSpan.FromMinutes(5);
        await store.EnqueueAsync("a", [1], expiresAt: soon);
        await store.EnqueueAsync("a", [2], expiresAt: later);
        await store.EnqueueAsync("b", [3], expiresAt: soon);
        await store.EnqueueAsync("b", [4], expiresAt: soon);
        var held = await store.TryDequeueAsync("b", TimeSpan.FromMinutes(5));
        held.ShouldNotBeNull();
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ExpireMessagesAll(time.GetUtcNow());

        sweep.ByQueue["a"].Count.ShouldBe(1);
        sweep.ByQueue["a"][0].EncodedMessage.ShouldBe(new byte[] { 1 });
        sweep.ByQueue["b"].Count.ShouldBe(1);
        sweep.ByQueue["b"][0].EncodedMessage.ShouldBe(new byte[] { 4 });
        sweep.HasPending.ShouldBeTrue("the locked expired message and the future-TTL message remain");
        (await store.CountAsync("a")).ShouldBe(1L);
        (await store.CountAsync("b")).ShouldBe(1L);
    }

    [Fact]
    public async Task ExpireMessagesAll_OnlyMessagesWithoutTtl_ReportsNothingPending()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1]);

        var sweep = store.ExpireMessagesAll(DateTimeOffset.UtcNow);

        sweep.ByQueue.ShouldBeEmpty();
        sweep.HasPending.ShouldBeFalse("no stored message carries a TTL");
    }

    [Fact]
    public async Task ActivateScheduledAll_ActivatesDueMessagesAcrossQueues_AndReportsPending()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", [1], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        await store.EnqueueAsync("a", [2], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromMinutes(10));
        await store.EnqueueAsync("b", [3], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ActivateScheduledAll(time.GetUtcNow());

        sweep.ByQueue["a"].ShouldBe(1);
        sweep.ByQueue["b"].ShouldBe(1);
        sweep.HasPending.ShouldBeTrue("one message is still scheduled for later");
        (await store.TryDequeueAsync("a", TimeSpan.FromSeconds(10)))!.Message.EncodedMessage.ShouldBe(new byte[] { 1 });
        (await store.TryDequeueAsync("b", TimeSpan.FromSeconds(10)))!.Message.EncodedMessage.ShouldBe(new byte[] { 3 });
    }

    [Fact]
    public async Task ActivateScheduledAll_SessionMessage_ActivatesIntoItsSessionChannel()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryMessageStore(time);
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [1], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5), sessionId: "s-1");
        time.Advance(TimeSpan.FromSeconds(30));

        var sweep = store.ActivateScheduledAll(time.GetUtcNow());

        sweep.ByQueue["q"].ShouldBe(1);
        (await store.TryAcceptSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link")).ShouldNotBeNull();
        var received = await store.TryDequeueFromSessionAsync("q", "s-1", TimeSpan.FromSeconds(30), "link");
        received.ShouldNotBeNull();
        received.Message.EncodedMessage.ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task PurgeAllAsync_ClearsEveryQueueAndTopicDedupHistory()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("a");
        await store.CreateQueueAsync("b");
        await store.EnqueueAsync("a", [1]);
        await store.EnqueueAsync("a", [2]);
        await store.EnqueueAsync("b", [3]);
        await store.TryDequeueAsync("b", TimeSpan.FromMinutes(5));
        var window = TimeSpan.FromMinutes(10);
        (await store.CheckTopicDuplicateAsync("t", "m-1", window)).ShouldBeFalse();

        var purged = await store.PurgeAllAsync();

        purged.ShouldBe(3L);
        (await store.CountAsync("a")).ShouldBe(0L);
        (await store.CountAsync("b")).ShouldBe(0L);
        (await store.CheckTopicDuplicateAsync("t", "m-1", window)).ShouldBeFalse(
            "a broker-wide purge must also forget topic duplicate-detection history");
    }

    [Fact]
    public async Task DefaultInterfaceSweeps_LoopThePerQueueApis_AndStayConservativeOnPending()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        IMessageStore store = new MinimalStore(new InMemoryMessageStore(time));
        await store.CreateQueueAsync("a");
        await store.EnqueueAsync("a", [1], expiresAt: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        await store.EnqueueAsync("a", [2], scheduledEnqueueTime: time.GetUtcNow() + TimeSpan.FromSeconds(5));
        await store.EnqueueAsync("a", [3]);
        await store.TryDequeueAsync("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(30));

        var locks = store.ExpireLocksAll(time.GetUtcNow());
        var messages = store.ExpireMessagesAll(time.GetUtcNow());
        var scheduled = store.ActivateScheduledAll(time.GetUtcNow());
        var purged = await store.PurgeAllAsync();

        locks.ByQueue["a"].ShouldBe(1);
        locks.HasPending.ShouldBeTrue("the default implementation cannot probe cheaply, so it must never report idle");
        messages.ByQueue["a"].Count.ShouldBe(1);
        messages.HasPending.ShouldBeTrue();
        scheduled.ByQueue["a"].ShouldBe(1);
        scheduled.HasPending.ShouldBeTrue();
        purged.ShouldBe(2L);
    }

    /// <summary>
    /// Forwards only the members the IMessageStore interface defaults need, so the test
    /// exercises the default ExpireLocksAll/ExpireMessagesAll/ActivateScheduledAll/PurgeAllAsync
    /// implementations rather than the InMemory overrides.
    /// </summary>
    private sealed class MinimalStore(InMemoryMessageStore inner) : IMessageStore
    {
        public Task CreateQueueAsync(string queueName, CancellationToken cancellationToken = default) => inner.CreateQueueAsync(queueName, cancellationToken);
        public Task DeleteQueueAsync(string queueName, CancellationToken cancellationToken = default) => inner.DeleteQueueAsync(queueName, cancellationToken);
        public IReadOnlyCollection<string> ListQueueNames() => inner.ListQueueNames();
        public Task<bool> CheckTopicDuplicateAsync(string topicName, string messageId, TimeSpan window, CancellationToken cancellationToken = default) => inner.CheckTopicDuplicateAsync(topicName, messageId, window, cancellationToken);
        public Task ClearTopicDedupHistoryAsync(string topicName, CancellationToken cancellationToken = default) => inner.ClearTopicDedupHistoryAsync(topicName, cancellationToken);
        public Task<Core.Messaging.StoredMessage> EnqueueAsync(string queueName, byte[] encodedMessage, DateTimeOffset? expiresAt = null, DateTimeOffset? scheduledEnqueueTime = null, string? sessionId = null, string? messageId = null, TimeSpan? duplicateDetectionWindow = null, int deliveryCount = 0, long? enqueuedSequenceNumber = null, CancellationToken cancellationToken = default) => inner.EnqueueAsync(queueName, encodedMessage, expiresAt, scheduledEnqueueTime, sessionId, messageId, duplicateDetectionWindow, deliveryCount, enqueuedSequenceNumber, cancellationToken);
        public Task<long> AllocateSequenceNumberAsync(string entityName, CancellationToken cancellationToken = default) => inner.AllocateSequenceNumberAsync(entityName, cancellationToken);
        public int ActivateScheduled(string queueName, DateTimeOffset now) => inner.ActivateScheduled(queueName, now);
        public Task<bool> TryCancelScheduledAsync(string queueName, long sequenceNumber, CancellationToken cancellationToken = default) => inner.TryCancelScheduledAsync(queueName, sequenceNumber, cancellationToken);
        public IReadOnlyList<Core.Messaging.StoredMessage> ExpireMessages(string queueName, DateTimeOffset now) => inner.ExpireMessages(queueName, now);
        public Task<long> CountAsync(string queueName, CancellationToken cancellationToken = default) => inner.CountAsync(queueName, cancellationToken);
        public Task<long> PurgeAsync(string queueName, CancellationToken cancellationToken = default) => inner.PurgeAsync(queueName, cancellationToken);
        public Task<Core.Messaging.LockedMessage?> TryDequeueAsync(string queueName, TimeSpan lockDuration, string? associatedLinkName = null, CancellationToken cancellationToken = default) => inner.TryDequeueAsync(queueName, lockDuration, associatedLinkName, cancellationToken);
        public Task<bool> TryCompleteAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default) => inner.TryCompleteAsync(queueName, lockToken, cancellationToken);
        public Task<bool> TryAbandonAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default) => inner.TryAbandonAsync(queueName, lockToken, cancellationToken);
        public int ExpireLocks(string queueName, DateTimeOffset now) => inner.ExpireLocks(queueName, now);
        public Task<DateTimeOffset?> TryRenewLockAsync(string queueName, Guid lockToken, TimeSpan lockDuration, string? requestingLinkName = null, CancellationToken cancellationToken = default) => inner.TryRenewLockAsync(queueName, lockToken, lockDuration, requestingLinkName, cancellationToken);
        public Task<Core.Messaging.StoredMessage?> TryRemoveLockedAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default) => inner.TryRemoveLockedAsync(queueName, lockToken, cancellationToken);
        public IReadOnlyList<Core.Messaging.StoredMessage> Peek(string queueName, long fromSequenceNumber, int maxCount) => inner.Peek(queueName, fromSequenceNumber, maxCount);
        public Task<bool> TryDeferAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default) => inner.TryDeferAsync(queueName, lockToken, cancellationToken);
        public Task<Core.Messaging.StoredMessage?> TryGetLockedAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default) => inner.TryGetLockedAsync(queueName, lockToken, cancellationToken);
        public Task<bool> TryUpdateLockedPayloadAsync(string queueName, Guid lockToken, byte[] encodedMessage, CancellationToken cancellationToken = default) => inner.TryUpdateLockedPayloadAsync(queueName, lockToken, encodedMessage, cancellationToken);
        public Task<Core.Messaging.LockedMessage?> TryReceiveDeferredAsync(string queueName, long sequenceNumber, TimeSpan lockDuration, string? associatedLinkName = null, CancellationToken cancellationToken = default) => inner.TryReceiveDeferredAsync(queueName, sequenceNumber, lockDuration, associatedLinkName, cancellationToken);
        public Task<Core.Messaging.SessionLock?> TryAcceptSessionAsync(string queueName, string sessionId, TimeSpan lockDuration, string? linkName = null, CancellationToken cancellationToken = default) => inner.TryAcceptSessionAsync(queueName, sessionId, lockDuration, linkName, cancellationToken);
        public Task<Core.Messaging.SessionLock?> TryAcceptNextSessionAsync(string queueName, TimeSpan lockDuration, string? linkName = null, CancellationToken cancellationToken = default) => inner.TryAcceptNextSessionAsync(queueName, lockDuration, linkName, cancellationToken);
        public Task<Core.Messaging.LockedMessage?> TryDequeueFromSessionAsync(string queueName, string sessionId, TimeSpan messageLockDuration, string? linkName = null, CancellationToken cancellationToken = default) => inner.TryDequeueFromSessionAsync(queueName, sessionId, messageLockDuration, linkName, cancellationToken);
        public Task<DateTimeOffset?> TryRenewSessionLockAsync(string queueName, string sessionId, TimeSpan lockDuration, string? requestingLinkName = null, CancellationToken cancellationToken = default) => inner.TryRenewSessionLockAsync(queueName, sessionId, lockDuration, requestingLinkName, cancellationToken);
        public Task ReleaseSessionAsync(string queueName, string sessionId, string? expectedLinkName = null, CancellationToken cancellationToken = default) => inner.ReleaseSessionAsync(queueName, sessionId, expectedLinkName, cancellationToken);
        public Task SetSessionStateAsync(string queueName, string sessionId, byte[]? state, CancellationToken cancellationToken = default) => inner.SetSessionStateAsync(queueName, sessionId, state, cancellationToken);
        public Task<byte[]?> GetSessionStateAsync(string queueName, string sessionId, CancellationToken cancellationToken = default) => inner.GetSessionStateAsync(queueName, sessionId, cancellationToken);
        public Task<bool> IsSessionLockHeldAsync(string queueName, string sessionId, string? linkName = null, CancellationToken cancellationToken = default) => inner.IsSessionLockHeldAsync(queueName, sessionId, linkName, cancellationToken);
        public IReadOnlyList<string> ListSessions(string queueName, DateTimeOffset? stateUpdatedAfter = null, int skip = 0, int top = int.MaxValue) => inner.ListSessions(queueName, stateUpdatedAfter, skip, top);
    }
}
