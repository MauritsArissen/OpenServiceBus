using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Transactions;

namespace OpenServiceBus.InMemoryStorage.Transactions;

/// <summary>
/// Default in-memory <see cref="ITransactionManager"/>. Each open txn keeps an in-order
/// list of deferred operations; commit validates all of them, then replays them
/// sequentially, under a per-txn lock so concurrent enlists during discharge can't slip
/// an op in between commit and forget. The same manager serves every
/// <see cref="OpenServiceBus.Core.Storage.IMessageStore"/> implementation - the buffered
/// operations capture the store they run against, so SQLite-backed brokers share it.
/// </summary>
public sealed class TransactionManager : ITransactionManager
{
    private readonly ConcurrentDictionary<TxnIdKey, OpenTxn> _open = new();
    private readonly ILogger<TransactionManager> _logger;

    public TransactionManager(ILogger<TransactionManager> logger)
    {
        _logger = logger;
    }

    public int OpenCount => _open.Count;

    public byte[] Declare()
    {
        // 8 random bytes is plenty for an in-memory broker; the AMQP txn-id is opaque binary.
        var id = new byte[8];
        RandomNumberGenerator.Fill(id);
        _open[new TxnIdKey(id)] = new OpenTxn();
        return id;
    }

    public bool Enlist(byte[] txnId, TransactionalOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_open.TryGetValue(new TxnIdKey(txnId), out var txn)) return false;

        lock (txn.SyncRoot)
        {
            if (txn.Settled) return false;
            txn.Operations.Add(operation);
            return true;
        }
    }

    public async Task CommitAsync(byte[] txnId, CancellationToken cancellationToken = default)
    {
        if (!_open.TryRemove(new TxnIdKey(txnId), out var txn)) return;

        TransactionalOperation[] toReplay;
        lock (txn.SyncRoot)
        {
            txn.Settled = true;
            toReplay = txn.Operations.ToArray();
        }

        // Phase 1 - validate everything before applying anything. A failing precondition
        // (lock expired, entity deleted) rejects the whole commit while the store is still
        // untouched, so the client's commit throws with no partial effects.
        foreach (var op in toReplay)
        {
            if (op.Validate is null) continue;
            try
            {
                await op.Validate(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Validation of a transactional op on '{Entity}' failed during commit of {TxnId}; rejecting the discharge with nothing applied",
                    op.Entity, FormatTxnId(txnId));
                throw;
            }
        }

        // Phase 2 - apply. Everything the supported operation set can fail on was checked
        // above, so a failure here is a genuine store fault (or a precondition lost in the
        // instants since validation). Stop and surface it so the discharge fails; ops
        // applied before the fault stay applied - the residual window is documented in
        // docs/Transactions.md.
        var applied = 0;
        foreach (var op in toReplay)
        {
            try
            {
                await op.Apply(cancellationToken).ConfigureAwait(false);
                applied++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Transactional op on '{Entity}' failed during commit of {TxnId} after {Applied}/{Total} op(s) applied; failing the discharge",
                    op.Entity, FormatTxnId(txnId), applied, toReplay.Length);
                throw;
            }
        }
    }

    public void Rollback(byte[] txnId)
    {
        if (!_open.TryRemove(new TxnIdKey(txnId), out var txn)) return;
        lock (txn.SyncRoot) { txn.Settled = true; txn.Operations.Clear(); }
    }

    private static string FormatTxnId(byte[] id) => Convert.ToHexString(id);

    private sealed class OpenTxn
    {
        public readonly object SyncRoot = new();
        public readonly List<TransactionalOperation> Operations = new();
        public bool Settled;
    }

    /// <summary>
    /// Byte-array equality wrapper so the dictionary keys on value, not reference. The AMQP
    /// peer sends back the same logical id but may use a different array instance.
    /// </summary>
    private readonly struct TxnIdKey : IEquatable<TxnIdKey>
    {
        private readonly byte[] _bytes;
        public TxnIdKey(byte[] bytes) { _bytes = bytes; }
        public bool Equals(TxnIdKey other) => _bytes.AsSpan().SequenceEqual(other._bytes);
        public override bool Equals(object? obj) => obj is TxnIdKey k && Equals(k);
        public override int GetHashCode()
        {
            // Simple FNV-1a over the bytes - collision risk is irrelevant at the scale we run.
            unchecked
            {
                int hash = (int)2166136261u;
                foreach (var b in _bytes) hash = (hash ^ b) * 16777619;
                return hash;
            }
        }
    }
}
