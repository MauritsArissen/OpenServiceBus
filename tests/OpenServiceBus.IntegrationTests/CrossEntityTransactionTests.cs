using System.Transactions;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Cross-entity transactions (issue #58): the SDK's <c>EnableCrossEntityTransactions</c>
/// puts every link - coordinator first - on one shared AMQP session. The broker pins that
/// session's via entity on the first entity attach, groups the buffered operations under
/// one txn so commit lands everything and rollback discards everything, and refuses
/// receives from a second entity with amqp:not-allowed, which the SDK surfaces as
/// <see cref="InvalidOperationException"/> - the same exception Azure produces.
/// </summary>
public class CrossEntityTransactionTests
{
    private static ServiceBusClientOptions CrossEntityOptions() => new()
    {
        EnableCrossEntityTransactions = true,
        RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(10), MaxRetries = 0 },
    };

    [Fact]
    public async Task ReceiveFromAAndSendToB_Committed_BothApplyAtomically()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-commit-a" });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-commit-b" });

        await using var seedClient = new ServiceBusClient(harness.ConnectionString);
        await seedClient.CreateSender("ce-commit-a").SendMessageAsync(new ServiceBusMessage("in") { MessageId = "ce-1" });

        await using var client = new ServiceBusClient(harness.ConnectionString, CrossEntityOptions());
        var receiverA = client.CreateReceiver("ce-commit-a");
        var senderB = client.CreateSender("ce-commit-b");

        // The receive is the session's first entity operation: A becomes the via entity.
        var msg = await receiverA.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await receiverA.CompleteMessageAsync(msg);
            await senderB.SendMessageAsync(new ServiceBusMessage("out") { MessageId = "ce-2" });
            // Nothing is visible before the commit.
            (await harness.Store.CountAsync("ce-commit-b")).ShouldBe(0L, "uncommitted cross-entity send must be invisible");
            scope.Complete();
        }

        (await TestUtilities.WaitForCountAsync(harness.Store, "ce-commit-b", expected: 1, timeoutMs: 2000))
            .ShouldBeTrue("the committed send must land on B");
        (await TestUtilities.WaitForCountAsync(harness.Store, "ce-commit-a", expected: 0, timeoutMs: 2000))
            .ShouldBeTrue("the committed complete must remove the message from A");
    }

    [Fact]
    public async Task ReceiveFromAAndSendToB_NotCompleted_BothRollBack()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-rb-a" });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-rb-b" });

        await using var seedClient = new ServiceBusClient(harness.ConnectionString);
        await seedClient.CreateSender("ce-rb-a").SendMessageAsync(new ServiceBusMessage("stay") { MessageId = "ce-rb-1" });

        await using var client = new ServiceBusClient(harness.ConnectionString, CrossEntityOptions());
        var receiverA = client.CreateReceiver("ce-rb-a");
        var senderB = client.CreateSender("ce-rb-b");

        var msg = await receiverA.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await receiverA.CompleteMessageAsync(msg);
            await senderB.SendMessageAsync(new ServiceBusMessage("never") { MessageId = "ce-rb-2" });
        }
        await Task.Delay(500);

        (await harness.Store.CountAsync("ce-rb-b")).ShouldBe(0L, "the rolled-back send must leave no trace on B");
        (await harness.Store.CountAsync("ce-rb-a")).ShouldBe(1L, "the rolled-back complete must keep the message on A");

        // The message is still locked and settleable - the buffered complete never applied,
        // so completing it again succeeds (the Azure SDK's own rollback test does the same).
        await receiverA.CompleteMessageAsync(msg);
        (await TestUtilities.WaitForCountAsync(harness.Store, "ce-rb-a", expected: 0, timeoutMs: 2000))
            .ShouldBeTrue("the post-rollback complete must remove the message");
    }

    [Fact]
    public async Task ReceiveFromSecondEntity_AfterViaEntityPinned_ThrowsInvalidOperationException()
    {
        // Mirrors the Azure SDK's own CrossEntityTransactionSendsFirst expectation: once the
        // first operation pins the via entity (B), receiving from A is refused and the SDK
        // surfaces the broker's amqp:not-allowed as InvalidOperationException.
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-via-a" });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-via-b" });

        await using var seedClient = new ServiceBusClient(harness.ConnectionString);
        await seedClient.CreateSender("ce-via-a").SendMessageAsync(new ServiceBusMessage("bait") { MessageId = "ce-via-1" });

        await using var client = new ServiceBusClient(harness.ConnectionString, CrossEntityOptions());
        var senderB = client.CreateSender("ce-via-b");
        var receiverA = client.CreateReceiver("ce-via-a");

        // First operation on the shared session: B is now the via entity.
        await senderB.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "ce-via-2" });

        await Should.ThrowAsync<InvalidOperationException>(
            () => receiverA.ReceiveMessageAsync(TimeSpan.FromSeconds(5)));

        // Receiving from the via entity itself still works on the same client.
        var receiverB = client.CreateReceiver("ce-via-b");
        var msg = await receiverB.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();
        msg.MessageId.ShouldBe("ce-via-2");
        await receiverB.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task CommitWithStaleLock_RejectsDischargeAndAppliesNothing()
    {
        // Replay-failure atomicity: the buffered send to B enlists BEFORE the buffered
        // complete on A, whose lock is allowed to expire. The commit's validation phase
        // must reject the discharge before the send applies - B stays empty, A keeps its
        // message, and the client sees the failed commit.
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "ce-stale-a",
            LockDuration = TimeSpan.FromSeconds(2),
        });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ce-stale-b" });

        await using var seedClient = new ServiceBusClient(harness.ConnectionString);
        await seedClient.CreateSender("ce-stale-a").SendMessageAsync(new ServiceBusMessage("stay") { MessageId = "ce-stale-1" });

        await using var client = new ServiceBusClient(harness.ConnectionString, CrossEntityOptions());
        var receiverA = client.CreateReceiver("ce-stale-a");
        var senderB = client.CreateSender("ce-stale-b");

        var msg = await receiverA.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        msg.ShouldNotBeNull();

        var commitFailed = false;
        try
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await senderB.SendMessageAsync(new ServiceBusMessage("must-not-land") { MessageId = "ce-stale-2" });
            await receiverA.CompleteMessageAsync(msg);
            // Let the peek-lock on A expire so the buffered complete can no longer apply.
            await Task.Delay(TimeSpan.FromSeconds(3));
            scope.Complete();
        }
        catch (TransactionException)
        {
            commitFailed = true;
        }

        commitFailed.ShouldBeTrue("the rejected discharge must surface as a failed commit");
        await Task.Delay(500);
        (await harness.Store.CountAsync("ce-stale-b")).ShouldBe(0L,
            "no operation of the rejected commit may apply - the send must not land on B");
        (await harness.Store.CountAsync("ce-stale-a")).ShouldBe(1L,
            "the message must remain on A after the rejected commit");
    }
}
