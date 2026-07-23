using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;
using NovaBank.Api.Domain;
using NovaBank.Api.Infrastructure;

namespace NovaBank.Api.Messaging;

/// <summary>
/// Turns customer-relevant events (selected by the subscription's SQL filter) into
/// notifications a customer would see in their banking app inbox.
/// </summary>
public sealed class NotificationWorker : SubscriptionWorker
{
    private readonly InMemoryBankStore _store;
    private readonly TimeProvider _time;

    public NotificationWorker(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        InMemoryBankStore store,
        TimeProvider time,
        ILogger<NotificationWorker> logger)
        : base(client, options.Value.EventsTopic, options.Value.NotificationsSubscription, logger)
    {
        _store = store;
        _time = time;
    }

    protected override Task HandleAsync(IntegrationEvent evt, ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        var data = evt.Data;
        switch (evt.EventType)
        {
            case EventTypes.TransferCompleted:
                Notify(GetString(data, "fromAccountId"), evt.EventType, "Transfer sent",
                    $"Your transfer of {GetString(data, "amount")} {GetString(data, "currency")} to {GetString(data, "toAccountId")} completed.");
                Notify(GetString(data, "toAccountId"), evt.EventType, "Money received",
                    $"You received {GetString(data, "amount")} {GetString(data, "currency")} from {GetString(data, "fromAccountId")}.");
                break;

            case EventTypes.TransferFailed:
                Notify(GetString(data, "fromAccountId"), evt.EventType, "Transfer failed",
                    $"Your transfer of {GetString(data, "amount")} {GetString(data, "currency")} failed: {GetString(data, "reason")}.");
                break;

            case EventTypes.AccountFrozen:
                Notify(GetString(data, "accountId"), evt.EventType, "Account frozen",
                    $"Your account was frozen. {GetString(data, "reason")}");
                break;

            case EventTypes.PaymentExecuted:
                Notify(GetString(data, "accountId"), evt.EventType, "Payment executed",
                    $"Your payment of {GetString(data, "amount")} {GetString(data, "currency")} to {GetString(data, "payeeName")} was executed.");
                break;

            case EventTypes.PaymentFailed:
                Notify(GetString(data, "accountId"), evt.EventType, "Payment failed",
                    $"Your payment of {GetString(data, "amount")} {GetString(data, "currency")} failed: {GetString(data, "reason")}.");
                break;
        }
        return Task.CompletedTask;
    }

    private void Notify(string? accountId, string eventType, string title, string body)
    {
        if (accountId is null) return;
        var account = _store.GetAccount(accountId);
        if (account is null) return;

        _store.AddNotification(new Notification
        {
            Id = $"NTF-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            CustomerId = account.CustomerId,
            AccountId = accountId,
            EventType = eventType,
            Title = title,
            Message = body,
            CreatedAtUtc = _time.GetUtcNow(),
        });
    }

    private static string? GetString(JsonElement data, string property)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
    }
}
