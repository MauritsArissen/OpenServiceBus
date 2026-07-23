using NovaBank.Api.Domain;

namespace NovaBank.Api.Contracts;

public sealed record CustomerResponse(string Id, string FullName, string Email, DateTimeOffset CreatedAtUtc)
{
    public static CustomerResponse From(Customer c) => new(c.Id, c.FullName, c.Email, c.CreatedAtUtc);
}

public sealed record AccountResponse(
    string Id, string CustomerId, string Currency, decimal Balance, string Status, DateTimeOffset OpenedAtUtc)
{
    public static AccountResponse From(Account a) =>
        new(a.Id, a.CustomerId, a.Currency, a.Balance, a.Status.ToString().ToLowerInvariant(), a.OpenedAtUtc);
}

public sealed record TransferResponse(
    string Id,
    string IdempotencyKey,
    string FromAccountId,
    string ToAccountId,
    decimal Amount,
    string Currency,
    string? Reference,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public static TransferResponse From(TransferRecord t) => new(
        t.Id, t.IdempotencyKey, t.FromAccountId, t.ToAccountId, t.Amount, t.Currency, t.Reference,
        t.Status.ToString().ToLowerInvariant(), t.FailureReason, t.CreatedAtUtc, t.CompletedAtUtc);
}

public sealed record PaymentResponse(
    string Id,
    string AccountId,
    string PayeeName,
    string PayeeIban,
    decimal Amount,
    string Currency,
    string? Reference,
    DateTimeOffset ExecuteAtUtc,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExecutedAtUtc,
    long? ExecutionOrder)
{
    public static PaymentResponse From(PaymentRecord p) => new(
        p.Id, p.AccountId, p.PayeeName, p.PayeeIban, p.Amount, p.Currency, p.Reference,
        p.ExecuteAtUtc, p.Status.ToString().ToLowerInvariant(), p.FailureReason,
        p.CreatedAtUtc, p.ExecutedAtUtc, p.ExecutionOrder);
}

public sealed record FraudAlertResponse(
    string Id, string AccountId, string EventType, decimal Amount, string Severity, bool AccountFrozen, DateTimeOffset CreatedAtUtc)
{
    public static FraudAlertResponse From(FraudAlert f) =>
        new(f.Id, f.AccountId, f.EventType, f.Amount, f.Severity, f.AccountFrozen, f.CreatedAtUtc);
}

public sealed record NotificationResponse(
    string Id, string CustomerId, string AccountId, string EventType, string Title, string Message, DateTimeOffset CreatedAtUtc)
{
    public static NotificationResponse From(Notification n) =>
        new(n.Id, n.CustomerId, n.AccountId, n.EventType, n.Title, n.Message, n.CreatedAtUtc);
}

public sealed record AuditEntryResponse(
    long Sequence, string EventId, string EventType, DateTimeOffset OccurredAtUtc, DateTimeOffset ReceivedAtUtc, string PayloadJson)
{
    public static AuditEntryResponse From(AuditEntry a) =>
        new(a.Sequence, a.EventId, a.EventType, a.OccurredAtUtc, a.ReceivedAtUtc, a.PayloadJson);
}

public sealed record DeadLetterMessageResponse(
    string MessageId,
    string? Subject,
    string Body,
    int DeliveryCount,
    string? DeadLetterReason,
    string? DeadLetterErrorDescription,
    DateTimeOffset EnqueuedTime);
