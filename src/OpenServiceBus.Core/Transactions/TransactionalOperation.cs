namespace OpenServiceBus.Core.Transactions;

/// <summary>
/// One operation buffered under an open transaction, split into a side-effect-free
/// <see cref="Validate"/> phase and the effectful <see cref="Apply"/> phase. Commit runs
/// every validation before any apply, so a precondition failure (lock expired, entity
/// deleted) rejects the discharge while the store is still untouched.
/// </summary>
public sealed class TransactionalOperation
{
    /// <summary>The entity the operation targets - used for commit diagnostics.</summary>
    public required string Entity { get; init; }

    /// <summary>
    /// Checks the operation's preconditions without mutating anything; throws when the
    /// operation could not apply. Null when the operation has nothing to pre-validate.
    /// </summary>
    public Func<CancellationToken, Task>? Validate { get; init; }

    /// <summary>The deferred store mutation, run once every operation in the transaction validated.</summary>
    public required Func<CancellationToken, Task> Apply { get; init; }
}
