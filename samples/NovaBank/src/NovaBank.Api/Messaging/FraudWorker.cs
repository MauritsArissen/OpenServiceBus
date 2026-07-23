using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;

namespace NovaBank.Api.Messaging;

/// <summary>
/// The broker does the pre-filtering: this subscription's SQL rule
/// (<c>amount &gt;= 10000</c>) only lets large *settled* movements through. Anything at or
/// above the freeze threshold gets the source account frozen and an account.frozen event
/// published (which itself carries no amount property, so it can never re-trigger fraud).
/// </summary>
public sealed class FraudWorker : SubscriptionWorker
{
    private readonly InMemoryBankStore _store;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _time;
    private readonly ILogger<FraudWorker> _logger;

    public FraudWorker(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        InMemoryBankStore store,
        IEventPublisher events,
        TimeProvider time,
        ILogger<FraudWorker> logger)
        : base(client, options.Value.EventsTopic, options.Value.FraudSubscription, logger)
    {
        _store = store;
        _events = events;
        _time = time;
        _logger = logger;
    }

    protected override async Task HandleAsync(IntegrationEvent evt, ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        var amount = message.ApplicationProperties.TryGetValue("amount", out var rawAmount)
            ? Convert.ToDecimal(rawAmount)
            : 0m;
        var accountId = message.ApplicationProperties.TryGetValue("accountId", out var rawAccount)
            ? rawAccount as string
            : null;
        if (accountId is null)
        {
            _logger.LogWarning("Fraud event {EventId} has no accountId - skipping.", evt.EventId);
            return;
        }

        var freeze = amount >= FraudRules.FreezeThreshold;
        _store.AddFraudAlert(new FraudAlert
        {
            Id = $"FRD-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            AccountId = accountId,
            EventType = evt.EventType,
            Amount = amount,
            Severity = freeze ? "critical" : "review",
            AccountFrozen = freeze,
            CreatedAtUtc = _time.GetUtcNow(),
        });
        _logger.LogWarning("Fraud alert: {EventType} of {Amount} on {AccountId} (severity={Severity}).",
            evt.EventType, amount, accountId, freeze ? "critical" : "review");

        if (freeze && _store.FreezeAccount(accountId))
        {
            await _events.PublishAsync(
                EventTypes.AccountFrozen,
                new
                {
                    accountId,
                    reason = $"Automated freeze: {evt.EventType} of {amount} met the {FraudRules.FreezeThreshold} review threshold.",
                    triggeredByEventId = evt.EventId,
                },
                accountId: accountId,
                cancellationToken: cancellationToken);
        }
    }
}
