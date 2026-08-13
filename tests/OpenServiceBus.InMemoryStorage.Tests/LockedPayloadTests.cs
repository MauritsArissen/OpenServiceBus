using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// The locked-payload accessors behind PropertiesToModify (issue #30): read and replace a
/// locked message's stored bytes without settling, keeping sequence number and delivery
/// count intact.
/// </summary>
public class LockedPayloadTests
{
    [Fact]
    public async Task TryGetLocked_ReturnsTheStoredMessage_OnlyWhileTheLockIsHeld()
    {
        var store = new InMemoryMessageStore();
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
    public async Task TryUpdateLockedPayload_ReplacesBytes_KeepsSequenceAndDeliveryCount()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        await store.EnqueueAsync("q", [0x01]);
        var locked = await store.TryDequeueAsync("q", TimeSpan.FromMinutes(1));
        locked.ShouldNotBeNull();

        (await store.TryUpdateLockedPayloadAsync("q", locked.LockToken, [0xAA, 0xBB])).ShouldBeTrue();
        await store.TryAbandonAsync("q", locked.LockToken);

        var redelivered = await store.TryDequeueAsync("q", TimeSpan.FromMinutes(1));
        redelivered.ShouldNotBeNull();
        redelivered.Message.SequenceNumber.ShouldBe(locked.Message.SequenceNumber);
        redelivered.Message.EncodedMessage.ShouldBe(new byte[] { 0xAA, 0xBB });
        redelivered.Message.DeliveryCount.ShouldBe(1, "abandon after the payload swap still bumps the delivery count");
    }

    [Fact]
    public async Task TryUpdateLockedPayload_UnknownToken_ReturnsFalse()
    {
        var store = new InMemoryMessageStore();
        await store.CreateQueueAsync("q");
        (await store.TryUpdateLockedPayloadAsync("q", Guid.NewGuid(), [0x01])).ShouldBeFalse();
    }
}
