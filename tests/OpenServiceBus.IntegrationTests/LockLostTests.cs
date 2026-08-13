using System.Transactions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Lock-lost surfaces as real errors instead of silent success (issue #52): settling or
/// renewing after the lock deadline throws <c>MessageLockLost</c> / <c>SessionLockLost</c>
/// through the real SDK, on the link-disposition path, the $management path, inside
/// transactions, and on the session receive pump.
/// </summary>
public class LockLostTests
{
    private static async Task<(OpenServiceBusTestHost Host, FakeTimeProvider Clock)> StartAsync(
        string queue, bool requiresSession = false)
    {
        var clock = new FakeTimeProvider();
        var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.CreateQueueAsync(new QueueDescriptor { Name = queue, RequiresSession = requiresSession });
        return (host, clock);
    }

    private static readonly TimeSpan PastTheLock = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Complete_AfterLockExpiry_ThrowsMessageLockLost_AndTheMessageRedelivers()
    {
        var (host, clock) = await StartAsync("ll-complete");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("ll-complete").SendMessageAsync(new ServiceBusMessage("x") { MessageId = "c-1" });

        var receiver = client.CreateReceiver("ll-complete");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        clock.Advance(PastTheLock);

        var ex = await Should.ThrowAsync<ServiceBusException>(() => receiver.CompleteMessageAsync(msg));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessageLockLost);

        var redelivered = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        redelivered.ShouldNotBeNull("the message must redeliver after the failed settle");
        redelivered.DeliveryCount.ShouldBe(2);
        await receiver.CompleteMessageAsync(redelivered);
    }

    [Fact]
    public async Task AbandonDeferAndDeadLetter_AfterLockExpiry_AllThrowMessageLockLost()
    {
        var (host, clock) = await StartAsync("ll-others");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("ll-others");
        var receiver = client.CreateReceiver("ll-others");

        foreach (var settle in new Func<ServiceBusReceiver, ServiceBusReceivedMessage, Task>[]
        {
            (r, m) => r.AbandonMessageAsync(m),
            (r, m) => r.DeferMessageAsync(m),
            (r, m) => r.DeadLetterMessageAsync(m, "reason", "desc"),
        })
        {
            await sender.SendMessageAsync(new ServiceBusMessage("x"));
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            msg.ShouldNotBeNull();
            clock.Advance(PastTheLock);

            var ex = await Should.ThrowAsync<ServiceBusException>(() => settle(receiver, msg));
            ex.Reason.ShouldBe(ServiceBusFailureReason.MessageLockLost);

            var redelivered = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            redelivered.ShouldNotBeNull();
            await receiver.CompleteMessageAsync(redelivered);
        }
    }

    [Fact]
    public async Task RenewMessageLock_AfterLockExpiry_ThrowsMessageLockLost()
    {
        var (host, clock) = await StartAsync("ll-renew");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("ll-renew").SendMessageAsync(new ServiceBusMessage("x"));

        var receiver = client.CreateReceiver("ll-renew");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        clock.Advance(PastTheLock);

        var ex = await Should.ThrowAsync<ServiceBusException>(() => receiver.RenewMessageLockAsync(msg));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessageLockLost);
    }

    [Fact]
    public async Task SettlingADeferredMessage_AfterItsLockExpiry_ThrowsMessageLockLost_ViaTheManagementPath()
    {
        var (host, clock) = await StartAsync("ll-mgmt");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("ll-mgmt").SendMessageAsync(new ServiceBusMessage("x"));

        var receiver = client.CreateReceiver("ll-mgmt");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.DeferMessageAsync(msg);

        // Settling a deferred message rides the $management update-disposition operation.
        var deferred = await receiver.ReceiveDeferredMessageAsync(msg.SequenceNumber);
        clock.Advance(PastTheLock);

        var ex = await Should.ThrowAsync<ServiceBusException>(() => receiver.CompleteMessageAsync(deferred));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessageLockLost);
    }

    [Fact]
    public async Task TransactionalComplete_WhoseLockExpiresBeforeCommit_FailsTheCommit()
    {
        var (host, clock) = await StartAsync("ll-txn");
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString, new ServiceBusClientOptions
        {
            EnableCrossEntityTransactions = false,
        });
        await client.CreateSender("ll-txn").SendMessageAsync(new ServiceBusMessage("x"));

        var receiver = client.CreateReceiver("ll-txn");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();

        await Should.ThrowAsync<TransactionException>(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await receiver.CompleteMessageAsync(msg);
            clock.Advance(PastTheLock);
            scope.Complete();
        });
    }

    [Fact]
    public async Task SessionSettle_AfterMessageLockExpiry_WithTheSessionStillHeld_ThrowsMessageLockLost()
    {
        var (host, clock) = await StartAsync("ll-session-msg", requiresSession: true);
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("ll-session-msg").SendMessageAsync(
            new ServiceBusMessage("x") { SessionId = "s1" });

        var session = await client.AcceptSessionAsync("ll-session-msg", "s1");
        var msg = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();

        // Keep the SESSION lock alive across the message lock's expiry: advance to just
        // before the deadline, renew the session, then advance past the message lock.
        clock.Advance(TimeSpan.FromSeconds(50));
        await session.RenewSessionLockAsync();
        clock.Advance(TimeSpan.FromSeconds(20));

        var ex = await Should.ThrowAsync<ServiceBusException>(() => session.CompleteMessageAsync(msg));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessageLockLost,
            "the session is still ours - only the message lock expired");
        await session.CloseAsync();
    }

    [Fact]
    public async Task SessionSettle_AfterTheSessionLockExpired_ThrowsSessionLockLost()
    {
        var (host, clock) = await StartAsync("ll-session-lost", requiresSession: true);
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("ll-session-lost").SendMessageAsync(
            new ServiceBusMessage("x") { SessionId = "s1" });

        var session = await client.AcceptSessionAsync("ll-session-lost", "s1");
        var msg = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        clock.Advance(PastTheLock);

        var ex = await Should.ThrowAsync<ServiceBusException>(() => session.CompleteMessageAsync(msg));
        ex.Reason.ShouldBe(ServiceBusFailureReason.SessionLockLost);
        await session.CloseAsync();
    }

    [Fact]
    public async Task RenewSessionLock_AfterExpiry_ThrowsSessionLockLost()
    {
        var (host, clock) = await StartAsync("ll-session-renew", requiresSession: true);
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        await client.CreateSender("ll-session-renew").SendMessageAsync(
            new ServiceBusMessage("x") { SessionId = "s1" });

        var session = await client.AcceptSessionAsync("ll-session-renew", "s1");
        clock.Advance(PastTheLock);

        var ex = await Should.ThrowAsync<ServiceBusException>(() => session.RenewSessionLockAsync());
        ex.Reason.ShouldBe(ServiceBusFailureReason.SessionLockLost);
        await session.CloseAsync();
    }

    [Fact]
    public async Task ExpiredSessionLock_IsFreeForTakeover_AndTheOriginalHoldersSettleThrowsSessionLockLost()
    {
        var (host, clock) = await StartAsync("ll-session-takeover", requiresSession: true);
        await using var _ = host;
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("ll-session-takeover");
        await sender.SendMessageAsync(new ServiceBusMessage("one") { SessionId = "s1" });
        await sender.SendMessageAsync(new ServiceBusMessage("two") { SessionId = "s1" });

        var original = await client.AcceptSessionAsync("ll-session-takeover", "s1");
        var held = await original.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        held.ShouldNotBeNull();
        clock.Advance(PastTheLock);

        // The expired lock is free: a second receiver accepts the same session.
        var takeover = await client.AcceptSessionAsync("ll-session-takeover", "s1");
        var next = await takeover.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        next.ShouldNotBeNull();
        await takeover.CompleteMessageAsync(next);

        // The original holder's in-flight message can no longer be settled.
        var ex = await Should.ThrowAsync<ServiceBusException>(() => original.CompleteMessageAsync(held));
        ex.Reason.ShouldBe(ServiceBusFailureReason.SessionLockLost);
        await takeover.CloseAsync();
    }
}
