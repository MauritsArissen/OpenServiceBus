using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;
using OpenServiceBus.Core.Entities;
using Shouldly;

namespace NovaBank.Api.Tests.Messaging;

/// <summary>
/// Unit tests for <see cref="TransferWorker"/> as a message consumer: no HTTP, no other
/// workers - just the worker class wired to a real broker. Commands go in via a raw
/// <see cref="ServiceBusSender"/>; assertions land on the store, the events topic, and
/// the broker's dead-letter queue.
/// </summary>
public class TransferWorkerBusTests : IClassFixture<ServiceBusFixture>, IAsyncLifetime
{
    private readonly ServiceBusFixture _bus;
    private InMemoryBankStore _store = null!;
    private BusSenders _senders = null!;
    private TransferWorker _worker = null!;

    public TransferWorkerBusTests(ServiceBusFixture bus) => _bus = bus;

    public async Task InitializeAsync()
    {
        _store = new InMemoryBankStore(TimeProvider.System);
        _senders = new BusSenders(_bus.Client, Options.Create(_bus.Names));
        _worker = new TransferWorker(
            _bus.Client,
            Options.Create(_bus.Names),
            _store,
            new ServiceBusEventPublisher(_senders, TimeProvider.System),
            NullLogger<TransferWorker>.Instance);
        await _worker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();
        await _senders.DisposeAsync();
    }

    private async Task<(TransferRecord Transfer, Account From, Account To)> SetUpTransferAsync(
        decimal fromBalance, decimal amount, string? reference = null)
    {
        var customer = _store.CreateCustomer("Bus Test", "bus@example.com");
        var from = _store.OpenAccount(customer.Id, "EUR", fromBalance);
        var to = _store.OpenAccount(customer.Id, "EUR", 0m);
        var key = Guid.NewGuid().ToString("N");
        var (transfer, _) = _store.GetOrCreateTransfer(key, () => new TransferRecord
        {
            Id = $"TRF-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            IdempotencyKey = key,
            FromAccountId = from.Id,
            ToAccountId = to.Id,
            Amount = amount,
            Currency = "EUR",
            Reference = reference,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        var command = new TransferCommand(transfer.Id, from.Id, to.Id, amount, "EUR", reference);
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(command, BusJson.Options))
        {
            MessageId = key,
        };
        await _senders.Transfers.SendMessageAsync(message);
        return (transfer, from, to);
    }

    [Fact]
    public async Task ConsumesCommand_MovesMoney_AndPublishesCompletionEvent()
    {
        var (transfer, from, to) = await SetUpTransferAsync(fromBalance: 1_000m, amount: 250m);

        await Eventually.SatisfiesAsync(
            () => Task.FromResult(_store.GetTransfer(transfer.Id)!.Status == TransferStatus.Completed),
            because: "the worker should settle the queued command");

        _store.GetAccount(from.Id)!.Balance.ShouldBe(750m);
        _store.GetAccount(to.Id)!.Balance.ShouldBe(250m);

        // The outcome event is on the topic, with the settled amount as a filterable property.
        var audit = _bus.Client.CreateReceiver(_bus.Names.EventsTopic, _bus.Names.AuditSubscription);
        var evt = await ServiceBusFixture.ReceiveUntilAsync(audit,
            m => (string)m.ApplicationProperties["eventType"] == "transfer.completed" &&
                 m.Body.ToString().Contains(transfer.Id));
        evt.ShouldNotBeNull();
        evt!.ApplicationProperties["amount"].ShouldBe(250d);

        // And the command itself is gone from the queue - completed, not lingering.
        // (Eventually: AutoComplete settles the message just AFTER the handler returns.)
        await Eventually.SatisfiesAsync(
            async () => await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue) == 0,
            because: "the settled command must be completed off the queue");
    }

    [Fact]
    public async Task BusinessFailure_CompletesTheMessage_AndPublishesTransferFailed()
    {
        var (transfer, from, _) = await SetUpTransferAsync(fromBalance: 10m, amount: 999m);

        await Eventually.SatisfiesAsync(
            () => Task.FromResult(_store.GetTransfer(transfer.Id)!.Status == TransferStatus.Failed),
            because: "insufficient funds is a business failure, settled on first delivery");

        _store.GetTransfer(transfer.Id)!.FailureReason.ShouldBe("insufficient_funds");
        _store.GetAccount(from.Id)!.Balance.ShouldBe(10m);

        // Business failures must NOT be retried or dead-lettered - the message is completed.
        // (Eventually: AutoComplete settles the message just AFTER the handler returns.)
        await Eventually.SatisfiesAsync(
            async () => await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue) == 0,
            because: "a business failure completes the message instead of retrying it");
        (await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue + "/$DeadLetterQueue")).ShouldBe(0L);
    }

    [Fact]
    public async Task PoisonCommand_IsRetriedToMaxDelivery_ThenDeadLettered_WithHistoryIntact()
    {
        var dlqBefore = await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue + "/$DeadLetterQueue");
        var (transfer, from, _) = await SetUpTransferAsync(fromBalance: 1_000m, amount: 100m, reference: "CHAOS");

        // The broker's own DLQ state is the assertion target - no API in between.
        await Eventually.SatisfiesAsync(
            async () => await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue + "/$DeadLetterQueue") == dlqBefore + 1,
            because: "3 failed deliveries (MaxDeliveryCount) should dead-letter the command");

        // Assert via PEEK: peek reports the stored delivery count verbatim, while a receive
        // would show +1 (the SDK's AMQP prior-deliveries adjustment on link deliveries).
        var dlqReceiver = _bus.Client.CreateReceiver(_bus.Names.TransfersQueue,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var peeked = await dlqReceiver.PeekMessagesAsync(maxMessages: 25);
        var poisoned = peeked.SingleOrDefault(m => m.Body.ToString().Contains(transfer.Id));

        poisoned.ShouldNotBeNull();
        poisoned!.DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
        poisoned.DeliveryCount.ShouldBe(3, "the DLQ copy keeps the delivery history");

        // Drain it so later tests see a clean DLQ.
        await ServiceBusFixture.ReceiveUntilAsync(dlqReceiver, m => m.Body.ToString().Contains(transfer.Id));

        _store.GetTransfer(transfer.Id)!.Status.ShouldBe(TransferStatus.Accepted, "no money may move for a poison command");
        _store.GetAccount(from.Id)!.Balance.ShouldBe(1_000m);
    }

    [Fact]
    public async Task CommandForUnknownTransfer_IsCompletedQuietly()
    {
        // Simulates a command surviving a restart of the (in-memory) API: the broker still
        // has it, the store doesn't. It must be swallowed, not retried forever.
        var command = new TransferCommand("TRF-GHOST", "ACC-A", "ACC-B", 10m, "EUR", null);
        await _senders.Transfers.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromObjectAsJson(command, BusJson.Options)));

        await Eventually.SatisfiesAsync(
            async () => await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue) == 0,
            because: "unknown-transfer commands should be completed, not redelivered");

        (await _bus.Bus.Store.CountAsync(_bus.Names.TransfersQueue + "/$DeadLetterQueue")).ShouldBe(0L);
    }
}
