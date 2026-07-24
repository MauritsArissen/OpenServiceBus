using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Core.Transactions;

namespace OpenServiceBus.InMemoryStorage.Transactions;

/// <summary>
/// Default in-memory <see cref="ITransactionManager"/>. Each open txn keeps an in-order
/// list of deferred operations; commit replays them sequentially under a per-txn lock so
/// concurrent enlists during discharge can't slip an op in between commit and forget.
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

    public bool Enlist(byte[] txnId, Func<CancellationToken, Task> operation)
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

        Func<CancellationToken, Task>[] toReplay;
        lock (txn.SyncRoot)
        {
            txn.Settled = true;
            toReplay = txn.Operations.ToArray();
        }

        foreach (var op in toReplay)
        {
            try
            {
                await op(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Stop on the first failure and surface it so the coordinator fails the
                // discharge - the client learns the commit did not fully apply rather than
                // being told it succeeded. Ops already replayed stay applied (the in-memory
                // store has no rollback), but stopping here keeps that footprint minimal.
                _logger.LogError(ex, "Transactional op failed during commit of {TxnId}; failing the discharge", FormatTxnId(txnId));
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
        public readonly List<Func<CancellationToken, Task>> Operations = new();
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
