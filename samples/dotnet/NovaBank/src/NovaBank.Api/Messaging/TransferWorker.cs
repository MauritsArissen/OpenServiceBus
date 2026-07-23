using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;

namespace NovaBank.Api.Messaging;

/// <summary>
/// Consumes <see cref="TransferCommand"/>s from the transfers queue, executes the money
/// movement, and publishes the outcome to the events topic.
///
/// Failure semantics: throwing abandons the message, the broker redelivers it, and after
/// MaxDeliveryCount attempts it lands on the dead-letter queue. A transfer whose
/// <c>reference</c> is <c>CHAOS</c> always throws - a built-in poison message for demoing
/// (and testing) the DLQ path end to end.
/// </summary>
public sealed class TransferWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly InMemoryBankStore _store;
    private readonly IEventPublisher _events;
    private readonly ILogger<TransferWorker> _logger;

    public TransferWorker(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        InMemoryBankStore store,
        IEventPublisher events,
        ILogger<TransferWorker> logger)
    {
        _client = client;
        _options = options.Value;
        _store = store;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = _client.CreateProcessor(_options.TransfersQueue, new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = true,
            MaxConcurrentCalls = 4,
        });

        processor.ProcessMessageAsync += OnMessageAsync;
        processor.ProcessErrorAsync += OnErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("TransferWorker listening on '{Queue}'.", _options.TransfersQueue);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutdown */ }

        await processor.StopProcessingAsync(CancellationToken.None);
        await processor.DisposeAsync();
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        var command = args.Message.Body.ToObjectFromJson<TransferCommand>(BusJson.Options);
        if (command is null)
        {
            _logger.LogWarning("Discarding unreadable transfer command {MessageId}.", args.Message.MessageId);
            return;
        }

        if (string.Equals(command.Reference, "CHAOS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Simulated downstream outage while processing transfer '{command.TransferId}' (reference=CHAOS).");
        }

        var transfer = _store.GetTransfer(command.TransferId);
        if (transfer is null)
        {
            // e.g. command survived a restart of this (in-memory) API instance.
            _logger.LogWarning("Transfer '{TransferId}' unknown to this instance - completing message.", command.TransferId);
            return;
        }
        if (transfer.Status != TransferStatus.Accepted)
        {
            // Replay guard. Broker-side duplicate detection should already have collapsed
            // idempotency-key retries; this keeps the worker safe regardless.
            return;
        }

        var outcome = _store.ExecuteTransfer(command.FromAccountId, command.ToAccountId, command.Amount, command.Currency);
        if (outcome == MoneyMoveOutcome.Completed)
        {
            _store.MarkTransferCompleted(command.TransferId);
            await _events.PublishAsync(
                EventTypes.TransferCompleted,
                new
                {
                    transferId = command.TransferId,
                    fromAccountId = command.FromAccountId,
                    toAccountId = command.ToAccountId,
                    amount = command.Amount,
                    currency = command.Currency,
                    reference = command.Reference,
                },
                settledAmount: command.Amount,
                accountId: command.FromAccountId,
                cancellationToken: args.CancellationToken);
            _logger.LogInformation("Transfer {TransferId} completed: {Amount} {Currency} {From} -> {To}.",
                command.TransferId, command.Amount, command.Currency, command.FromAccountId, command.ToAccountId);
        }
        else
        {
            var reason = outcome.ToReason();
            _store.MarkTransferFailed(command.TransferId, reason);
            await _events.PublishAsync(
                EventTypes.TransferFailed,
                new
                {
                    transferId = command.TransferId,
                    fromAccountId = command.FromAccountId,
                    toAccountId = command.ToAccountId,
                    amount = command.Amount,
                    currency = command.Currency,
                    reason,
                },
                accountId: command.FromAccountId,
                cancellationToken: args.CancellationToken);
            _logger.LogWarning("Transfer {TransferId} failed: {Reason}.", command.TransferId, reason);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogWarning(args.Exception,
            "Transfer processor error (source={Source}, entity={Entity}).", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
