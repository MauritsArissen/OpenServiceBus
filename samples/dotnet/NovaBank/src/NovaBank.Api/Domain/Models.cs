namespace NovaBank.Api.Domain;

public enum AccountStatus
{
    Active,
    Frozen,
}

public sealed class Customer
{
    public required string Id { get; init; }
    public required string FullName { get; init; }
    public required string Email { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class Account
{
    public required string Id { get; init; }
    public required string CustomerId { get; init; }
    public required string Currency { get; init; }
    public decimal Balance { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public DateTimeOffset OpenedAtUtc { get; init; }
}

public enum TransferStatus
{
    Accepted,
    Completed,
    Failed,
}

public sealed class TransferRecord
{
    public required string Id { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string FromAccountId { get; init; }
    public required string ToAccountId { get; init; }
    public decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? Reference { get; init; }
    public TransferStatus Status { get; set; } = TransferStatus.Accepted;
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum PaymentStatus
{
    Scheduled,
    Queued,
    Executed,
    Failed,
}

public sealed class PaymentRecord
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string PayeeName { get; init; }
    public required string PayeeIban { get; init; }
    public decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? Reference { get; init; }
    public DateTimeOffset ExecuteAtUtc { get; init; }
    public PaymentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ExecutedAtUtc { get; set; }

    /// <summary>Global monotonic counter stamped when the payment executes - lets tests
    /// (and curious humans) verify per-account FIFO ordering of the session queue.</summary>
    public long? ExecutionOrder { get; set; }
}

public sealed class FraudAlert
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string EventType { get; init; }
    public decimal Amount { get; init; }
    public required string Severity { get; init; }
    public bool AccountFrozen { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class Notification
{
    public required string Id { get; init; }
    public required string CustomerId { get; init; }
    public required string AccountId { get; init; }
    public required string EventType { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed class AuditEntry
{
    public long Sequence { get; init; }
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public required string PayloadJson { get; init; }
}

public enum MoneyMoveOutcome
{
    Completed,
    SourceNotFound,
    DestinationNotFound,
    SourceFrozen,
    DestinationFrozen,
    CurrencyMismatch,
    InsufficientFunds,
}

public static class MoneyMoveOutcomeExtensions
{
    public static string ToReason(this MoneyMoveOutcome outcome) => outcome switch
    {
        MoneyMoveOutcome.SourceNotFound => "source_account_not_found",
        MoneyMoveOutcome.DestinationNotFound => "destination_account_not_found",
        MoneyMoveOutcome.SourceFrozen => "source_account_frozen",
        MoneyMoveOutcome.DestinationFrozen => "destination_account_frozen",
        MoneyMoveOutcome.CurrencyMismatch => "currency_mismatch",
        MoneyMoveOutcome.InsufficientFunds => "insufficient_funds",
        _ => "ok",
    };
}

/// <summary>Thresholds the fraud engine applies to settled money movements.</summary>
public static class FraudRules
{
    public const decimal ReviewThreshold = 10_000m;
    public const decimal FreezeThreshold = 25_000m;
}
