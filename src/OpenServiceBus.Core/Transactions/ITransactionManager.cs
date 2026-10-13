namespace OpenServiceBus.Core.Transactions;

/// <summary>
/// Buffers operations that are part of an AMQP transaction. Senders and receivers
/// detect <c>TransactionalState</c> on a delivery and call <see cref="Enlist"/> instead of
/// executing immediately; the coordinator link processor calls <see cref="CommitAsync"/> or
/// <see cref="Rollback"/> when the client discharges the txn.
///
/// Operations within a single txn replay in order on commit. Commit is two-phase: every
/// operation's <see cref="TransactionalOperation.Validate"/> runs before any
/// <see cref="TransactionalOperation.Apply"/>, so a precondition failure rejects the
/// discharge with no effects applied. Rollback simply discards.
/// </summary>
public interface ITransactionManager
{
    /// <summary>Allocate a fresh transaction id. The bytes are returned to the client in <c>Declared</c>.</summary>
    byte[] Declare();

    /// <summary>
    /// Append <paramref name="operation"/> to the named transaction. Returns false if the
    /// txn id is unknown - typically because <see cref="CommitAsync"/>/<see cref="Rollback"/>
    /// already ran. The caller should reject the wire-level frame in that case.
    /// </summary>
    bool Enlist(byte[] txnId, TransactionalOperation operation);

    /// <summary>
    /// Replay every enlisted op in order and forget the txn. No-op on unknown txn id.
    /// Validates every op first and throws without applying anything when a validation
    /// fails; an apply-phase fault (a genuine store error after validation passed) still
    /// stops the replay and rethrows so the coordinator can fail the discharge.
    /// </summary>
    Task CommitAsync(byte[] txnId, CancellationToken cancellationToken = default);

    /// <summary>Discard every enlisted op and forget the txn. No-op on unknown txn id.</summary>
    void Rollback(byte[] txnId);

    /// <summary>Total open transactions - exposed for tests + diagnostics.</summary>
    int OpenCount { get; }
}
