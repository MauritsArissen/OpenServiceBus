using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;
using NovaBank.Api.Contracts;
using NovaBank.Api.Infrastructure;

namespace NovaBank.Api.Api;

/// <summary>Back-office surface: audit trail, fraud desk, and dead-letter inspection.</summary>
public static class OperationsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", (string? eventType, InMemoryBankStore store) =>
            Results.Ok(store.ListAudit(eventType).Select(AuditEntryResponse.From)))
        .WithTags("Operations")
        .WithSummary("Full audit trail")
        .WithDescription("Every event published to the topic, in delivery order (match-all audit subscription).");

        app.MapGet("/api/fraud/alerts", (string? accountId, InMemoryBankStore store) =>
            Results.Ok(store.ListFraudAlerts(accountId).Select(FraudAlertResponse.From)))
        .WithTags("Operations")
        .WithSummary("Fraud desk alerts")
        .WithDescription("Raised by the fraud subscription (SQL filter: amount >= 10000).");

        app.MapGet("/api/admin/dead-letters/{queueName}", async (
            string queueName,
            ServiceBusClient client,
            IOptions<ServiceBusOptions> options) =>
        {
            // Only the app's own queues are inspectable.
            var allowed = new[] { options.Value.TransfersQueue, options.Value.PaymentsQueue };
            if (!allowed.Contains(queueName, StringComparer.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"Unknown queue '{queueName}'.", allowed });
            }

            await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
            {
                SubQueue = SubQueue.DeadLetter,
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });
            var messages = await receiver.PeekMessagesAsync(maxMessages: 25);

            return Results.Ok(messages.Select(m => new DeadLetterMessageResponse(
                m.MessageId,
                m.Subject,
                m.Body.ToString(),
                (int)m.DeliveryCount,
                m.DeadLetterReason,
                m.DeadLetterErrorDescription,
                m.EnqueuedTime)));
        })
        .WithTags("Operations")
        .WithSummary("Peek a queue's dead-letter queue")
        .WithDescription("Shows poisoned messages (e.g. transfers with reference=CHAOS after MaxDeliveryCount attempts).");

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
            .WithTags("Operations")
            .WithSummary("Liveness probe");
    }
}
