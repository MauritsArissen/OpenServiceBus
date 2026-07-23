using System.Collections.Concurrent;
using NovaBank.Api.Domain;

namespace NovaBank.Api.Infrastructure;

/// <summary>
/// In-memory system of record for the sample. A single gate serializes money movements so
/// debit+credit pairs are atomic; everything else lives in concurrent dictionaries.
/// </summary>
public sealed class InMemoryBankStore
{
    private readonly object _moneyGate = new();
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, Customer> _customers = new();
    private readonly ConcurrentDictionary<string, Account> _accounts = new();
    private readonly ConcurrentDictionary<string, TransferRecord> _transfers = new();
    private readonly ConcurrentDictionary<string, string> _transfersByIdempotencyKey = new();
    private readonly ConcurrentDictionary<string, PaymentRecord> _payments = new();
    private readonly List<AuditEntry> _audit = [];
    private readonly List<FraudAlert> _fraudAlerts = [];
    private readonly List<Notification> _notifications = [];
    private long _auditSequence;
    private long _executionOrder;

    public InMemoryBankStore(TimeProvider time) => _time = time;

    // ---- customers -------------------------------------------------------------------

    public Customer CreateCustomer(string fullName, string email)
    {
        var customer = new Customer
        {
            Id = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            FullName = fullName,
            Email = email,
            CreatedAtUtc = _time.GetUtcNow(),
        };
        _customers[customer.Id] = customer;
        return customer;
    }

    public Customer? GetCustomer(string id) => _customers.GetValueOrDefault(id);

    public IReadOnlyList<Customer> ListCustomers() =>
        _customers.Values.OrderBy(c => c.CreatedAtUtc).ToList();

    // ---- accounts --------------------------------------------------------------------

    public Account OpenAccount(string customerId, string currency, decimal openingBalance)
    {
        var account = new Account
        {
            Id = $"ACC-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            CustomerId = customerId,
            Currency = currency.ToUpperInvariant(),
            Balance = openingBalance,
            OpenedAtUtc = _time.GetUtcNow(),
        };
        _accounts[account.Id] = account;
        return account;
    }

    /// <summary>Seed helper - registers an account with a fixed, human-friendly id.</summary>
    public Account AddAccount(Account account)
    {
        _accounts[account.Id] = account;
        return account;
    }

    public Customer AddCustomer(Customer customer)
    {
        _customers[customer.Id] = customer;
        return customer;
    }

    public Account? GetAccount(string id) => _accounts.GetValueOrDefault(id);

    public IReadOnlyList<Account> ListAccounts(string? customerId = null) =>
        _accounts.Values
            .Where(a => customerId is null || a.CustomerId == customerId)
            .OrderBy(a => a.OpenedAtUtc)
            .ToList();

    public MoneyMoveOutcome Deposit(string accountId, decimal amount)
    {
        lock (_moneyGate)
        {
            var account = GetAccount(accountId);
            if (account is null) return MoneyMoveOutcome.DestinationNotFound;
            if (account.Status == AccountStatus.Frozen) return MoneyMoveOutcome.DestinationFrozen;
            account.Balance += amount;
            return MoneyMoveOutcome.Completed;
        }
    }

    public MoneyMoveOutcome Withdraw(string accountId, decimal amount, string? currency = null)
    {
        lock (_moneyGate)
        {
            var account = GetAccount(accountId);
            if (account is null) return MoneyMoveOutcome.SourceNotFound;
            if (account.Status == AccountStatus.Frozen) return MoneyMoveOutcome.SourceFrozen;
            if (currency is not null &&
                !string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                return MoneyMoveOutcome.CurrencyMismatch;
            }
            if (account.Balance < amount) return MoneyMoveOutcome.InsufficientFunds;
            account.Balance -= amount;
            return MoneyMoveOutcome.Completed;
        }
    }

    public MoneyMoveOutcome ExecuteTransfer(string fromId, string toId, decimal amount, string currency)
    {
        lock (_moneyGate)
        {
            var from = GetAccount(fromId);
            if (from is null) return MoneyMoveOutcome.SourceNotFound;
            var to = GetAccount(toId);
            if (to is null) return MoneyMoveOutcome.DestinationNotFound;
            if (from.Status == AccountStatus.Frozen) return MoneyMoveOutcome.SourceFrozen;
            if (to.Status == AccountStatus.Frozen) return MoneyMoveOutcome.DestinationFrozen;
            if (!string.Equals(from.Currency, currency, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(to.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                return MoneyMoveOutcome.CurrencyMismatch;
            }
            if (from.Balance < amount) return MoneyMoveOutcome.InsufficientFunds;

            from.Balance -= amount;
            to.Balance += amount;
            return MoneyMoveOutcome.Completed;
        }
    }

    /// <summary>Freeze an account. Returns true when this call performed the freeze
    /// (false when the account was already frozen or doesn't exist) so callers can
    /// publish the account.frozen event exactly once.</summary>
    public bool FreezeAccount(string accountId)
    {
        lock (_moneyGate)
        {
            var account = GetAccount(accountId);
            if (account is null || account.Status == AccountStatus.Frozen) return false;
            account.Status = AccountStatus.Frozen;
            return true;
        }
    }

    // ---- transfers ---------------------------------------------------------------------

    /// <summary>Idempotent create: the first caller with a given key wins, later callers get
    /// the existing record back. Mirrors how a payments API deduplicates client retries.</summary>
    public (TransferRecord Transfer, bool Created) GetOrCreateTransfer(string idempotencyKey, Func<TransferRecord> factory)
    {
        while (true)
        {
            if (_transfersByIdempotencyKey.TryGetValue(idempotencyKey, out var existingId) &&
                _transfers.TryGetValue(existingId, out var existing))
            {
                return (existing, false);
            }

            var record = factory();
            if (_transfersByIdempotencyKey.TryAdd(idempotencyKey, record.Id))
            {
                _transfers[record.Id] = record;
                return (record, true);
            }
        }
    }

    public TransferRecord? GetTransfer(string id) => _transfers.GetValueOrDefault(id);

    public IReadOnlyList<TransferRecord> ListTransfers() =>
        _transfers.Values.OrderBy(t => t.CreatedAtUtc).ToList();

    public void MarkTransferCompleted(string id)
    {
        if (_transfers.TryGetValue(id, out var t))
        {
            t.Status = TransferStatus.Completed;
            t.CompletedAtUtc = _time.GetUtcNow();
        }
    }

    public void MarkTransferFailed(string id, string reason)
    {
        if (_transfers.TryGetValue(id, out var t))
        {
            t.Status = TransferStatus.Failed;
            t.FailureReason = reason;
            t.CompletedAtUtc = _time.GetUtcNow();
        }
    }

    // ---- payments ----------------------------------------------------------------------

    public PaymentRecord AddPayment(PaymentRecord payment)
    {
        _payments[payment.Id] = payment;
        return payment;
    }

    public PaymentRecord? GetPayment(string id) => _payments.GetValueOrDefault(id);

    public IReadOnlyList<PaymentRecord> ListPayments(string? accountId = null) =>
        _payments.Values
            .Where(p => accountId is null || p.AccountId == accountId)
            .OrderBy(p => p.CreatedAtUtc)
            .ToList();

    public void MarkPaymentExecuted(string id)
    {
        if (_payments.TryGetValue(id, out var p))
        {
            p.Status = PaymentStatus.Executed;
            p.ExecutedAtUtc = _time.GetUtcNow();
            p.ExecutionOrder = Interlocked.Increment(ref _executionOrder);
        }
    }

    public void MarkPaymentFailed(string id, string reason)
    {
        if (_payments.TryGetValue(id, out var p))
        {
            p.Status = PaymentStatus.Failed;
            p.FailureReason = reason;
            p.ExecutedAtUtc = _time.GetUtcNow();
            p.ExecutionOrder = Interlocked.Increment(ref _executionOrder);
        }
    }

    // ---- projections fed by the topic subscriptions --------------------------------------

    public void AddAudit(string eventId, string eventType, DateTimeOffset occurredAtUtc, string payloadJson)
    {
        lock (_audit)
        {
            _audit.Add(new AuditEntry
            {
                Sequence = ++_auditSequence,
                EventId = eventId,
                EventType = eventType,
                OccurredAtUtc = occurredAtUtc,
                ReceivedAtUtc = _time.GetUtcNow(),
                PayloadJson = payloadJson,
            });
        }
    }

    public IReadOnlyList<AuditEntry> ListAudit(string? eventType = null)
    {
        lock (_audit)
        {
            return _audit
                .Where(a => eventType is null || a.EventType == eventType)
                .ToList();
        }
    }

    public FraudAlert AddFraudAlert(FraudAlert alert)
    {
        lock (_fraudAlerts) { _fraudAlerts.Add(alert); }
        return alert;
    }

    public IReadOnlyList<FraudAlert> ListFraudAlerts(string? accountId = null)
    {
        lock (_fraudAlerts)
        {
            return _fraudAlerts
                .Where(f => accountId is null || f.AccountId == accountId)
                .ToList();
        }
    }

    public Notification AddNotification(Notification notification)
    {
        lock (_notifications) { _notifications.Add(notification); }
        return notification;
    }

    public IReadOnlyList<Notification> ListNotifications(string? customerId = null)
    {
        lock (_notifications)
        {
            return _notifications
                .Where(n => customerId is null || n.CustomerId == customerId)
                .ToList();
        }
    }
}
