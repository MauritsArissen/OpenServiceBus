namespace NovaBank.Api.Contracts;

public sealed record CreateCustomerRequest(string FullName, string Email);

public sealed record OpenAccountRequest(string CustomerId, string Currency, decimal OpeningBalance);

public sealed record MoneyRequest(decimal Amount);

public sealed record CreateTransferRequest(
    string FromAccountId,
    string ToAccountId,
    decimal Amount,
    string Currency,
    string? Reference);

public sealed record CreatePaymentRequest(
    string AccountId,
    string PayeeName,
    string PayeeIban,
    decimal Amount,
    string Currency,
    string? Reference,
    /// <summary>When set to a future instant the instruction is scheduled on the broker
    /// (ScheduleMessageAsync) and only becomes visible to the worker at that time.</summary>
    DateTimeOffset? ExecuteAtUtc);
