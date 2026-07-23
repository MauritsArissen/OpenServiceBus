using System.Text.Json;

namespace NovaBank.Api.Messaging;

/// <summary>Command consumed from the transfers queue by <see cref="TransferWorker"/>.</summary>
public sealed record TransferCommand(
    string TransferId,
    string FromAccountId,
    string ToAccountId,
    decimal Amount,
    string Currency,
    string? Reference);

/// <summary>Command consumed from the (session-enabled) payments queue by <see cref="PaymentWorker"/>.</summary>
public sealed record PaymentInstruction(
    string PaymentId,
    string AccountId,
    string PayeeName,
    string PayeeIban,
    decimal Amount,
    string Currency,
    string? Reference);

/// <summary>Envelope for every event published to the events topic.</summary>
public sealed record IntegrationEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    JsonElement Data);

/// <summary>Well-known event type names (also used in subscription SQL filters).</summary>
public static class EventTypes
{
    public const string CustomerCreated = "customer.created";
    public const string AccountOpened = "account.opened";
    public const string AccountDeposited = "account.deposited";
    public const string AccountWithdrawn = "account.withdrawn";
    public const string AccountFrozen = "account.frozen";
    public const string TransferRequested = "transfer.requested";
    public const string TransferCompleted = "transfer.completed";
    public const string TransferFailed = "transfer.failed";
    public const string PaymentScheduled = "payment.scheduled";
    public const string PaymentExecuted = "payment.executed";
    public const string PaymentFailed = "payment.failed";
}
