using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;
using NovaBank.Api.Infrastructure;

namespace NovaBank.Api.Messaging;

/// <summary>Writes every event on the topic (the audit subscription has a match-all rule)
/// into an append-only, sequence-numbered audit trail.</summary>
public sealed class AuditWorker : SubscriptionWorker
{
    private readonly InMemoryBankStore _store;

    public AuditWorker(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        InMemoryBankStore store,
        ILogger<AuditWorker> logger)
        : base(client, options.Value.EventsTopic, options.Value.AuditSubscription, logger)
    {
        _store = store;
    }

    protected override Task HandleAsync(IntegrationEvent evt, ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        _store.AddAudit(evt.EventId, evt.EventType, evt.OccurredAtUtc, evt.Data.GetRawText());
        return Task.CompletedTask;
    }
}
