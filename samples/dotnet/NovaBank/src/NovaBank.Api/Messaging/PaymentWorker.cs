using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;

namespace NovaBank.Api.Messaging;

/// <summary>
/// Session processor over the payments queue. SessionId = accountId, so payments for one
/// account always execute in FIFO order while different accounts run concurrently -
/// exactly the standing-order semantics a bank needs.
/// </summary>
public sealed class PaymentWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly InMemoryBankStore _store;
    private readonly IEventPublisher _events;
    private readonly ILogger<PaymentWorker> _logger;

    public PaymentWorker(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        InMemoryBankStore store,
        IEventPublisher events,
        ILogger<PaymentWorker> logger)
    {
        _client = client;
        _options = options.Value;
        _store = store;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = _client.CreateSessionProcessor(_options.PaymentsQueue, new ServiceBusSessionProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = true,
            MaxConcurrentSessions = 4,
            MaxConcurrentCallsPerSession = 1,
            // Release an idle session quickly so newly arriving sessions get a slot.
            SessionIdleTimeout = TimeSpan.FromSeconds(2),
        });

        processor.ProcessMessageAsync += OnMessageAsync;
        processor.ProcessErrorAsync += OnErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("PaymentWorker listening on session queue '{Queue}'.", _options.PaymentsQueue);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutdown */ }

        await processor.StopProcessingAsync(CancellationToken.None);
        await processor.DisposeAsync();
    }

    private async Task OnMessageAsync(ProcessSessionMessageEventArgs args)
    {
        var instruction = args.Message.Body.ToObjectFromJson<PaymentInstruction>(BusJson.Options);
        if (instruction is null)
        {
            _logger.LogWarning("Discarding unreadable payment instruction {MessageId}.", args.Message.MessageId);
            return;
        }

        var payment = _store.GetPayment(instruction.PaymentId);
        if (payment is null)
        {
            _logger.LogWarning("Payment '{PaymentId}' unknown to this instance - completing message.", instruction.PaymentId);
            return;
        }
        if (payment.Status is PaymentStatus.Executed or PaymentStatus.Failed)
        {
            return;
        }

        var outcome = _store.Withdraw(instruction.AccountId, instruction.Amount, instruction.Currency);
        if (outcome == MoneyMoveOutcome.Completed)
        {
            _store.MarkPaymentExecuted(instruction.PaymentId);
            await _events.PublishAsync(
                EventTypes.PaymentExecuted,
                new
                {
                    paymentId = instruction.PaymentId,
                    accountId = instruction.AccountId,
                    payeeName = instruction.PayeeName,
                    payeeIban = instruction.PayeeIban,
                    amount = instruction.Amount,
                    currency = instruction.Currency,
                    reference = instruction.Reference,
                },
                settledAmount: instruction.Amount,
                accountId: instruction.AccountId,
                cancellationToken: args.CancellationToken);
            _logger.LogInformation("Payment {PaymentId} executed: {Amount} {Currency} from {Account} (session={Session}).",
                instruction.PaymentId, instruction.Amount, instruction.Currency, instruction.AccountId, args.SessionId);
        }
        else
        {
            var reason = outcome.ToReason();
            _store.MarkPaymentFailed(instruction.PaymentId, reason);
            await _events.PublishAsync(
                EventTypes.PaymentFailed,
                new
                {
                    paymentId = instruction.PaymentId,
                    accountId = instruction.AccountId,
                    amount = instruction.Amount,
                    currency = instruction.Currency,
                    reason,
                },
                accountId: instruction.AccountId,
                cancellationToken: args.CancellationToken);
            _logger.LogWarning("Payment {PaymentId} failed: {Reason}.", instruction.PaymentId, reason);
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogWarning(args.Exception,
            "Payment processor error (source={Source}, entity={Entity}).", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }
}
