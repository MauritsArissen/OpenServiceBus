using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.SqliteStorage;

/// <summary>
/// SQLite-backed <see cref="IMessageStore"/>. Single-file persistence with the same semantics
/// as <c>InMemoryMessageStore</c> - peek-lock, dead-letter routing handled at the layer above,
/// sessions, dedup, scheduled, TTL, defer, peek. Survives broker restarts.
///
/// Concurrency model: SQLite serialises writers; multiple readers are fine. We open a
/// single connection (with WAL) and serialise all access through one async lock to keep
/// the implementation simple. With WAL on a real file, read concurrency could be unlocked
/// later - the gating bottleneck for now is the lone connection.
///
/// Long-poll dequeue uses an in-process per-queue <see cref="Channel{T}"/> as a notification
/// channel: every enqueue / abandon / scheduled-activation writes to it, and
/// <see cref="TryDequeueAsync"/> waits on it (with a polling timeout fallback) before
/// re-running the claim query.
/// </summary>
public sealed class SqliteMessageStore : IMessageStore, IAsyncDisposable
{
    private readonly SqliteStorageOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteMessageStore> _logger;

    private readonly SqliteConnection _connection;
    // All writes (and reads, for simplicity) go through this lock. SQLite serialises writers
    // at the DB level anyway; doing it here lets us avoid SqliteException busy retries.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Per-queue notification channels - used by TryDequeueAsync to wake on enqueue.
    // Bounded capacity of 1 with DropWrite means multiple signals collapse into "go check"
    // without piling up; the dequeue path can drain at its own pace.
    private readonly ConcurrentDictionary<string, Channel<bool>> _notify =
        new(StringComparer.OrdinalIgnoreCase);

    // Cumulative per-queue metrics counters (process-lifetime, in-memory - metrics are
    // ephemeral by design). Enqueued = new arrivals; Completed = successful settlements.
    private readonly ConcurrentDictionary<string, long> _enqueuedTotal = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _completedTotal = new(StringComparer.OrdinalIgnoreCase);

    public SqliteMessageStore(IOptions<SqliteStorageOptions> options, ILogger<SqliteMessageStore> logger)
        : this(options.Value, TimeProvider.System, logger) { }

    public SqliteMessageStore(SqliteStorageOptions options, TimeProvider timeProvider, ILogger<SqliteMessageStore> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;

        var dataSource = options.DataSource == ":memory:"
            // Shared-cache so the same logical DB is visible to every connection - important
            // if we ever open >1 connection (we don't today, but a shared in-memory DB also
            // survives one connection being closed which makes tests cleaner).
            ? $"file:osb-{Guid.NewGuid():N}?mode=memory&cache=shared"
            : options.DataSource;

        var csb = new SqliteConnectionStringBuilder { DataSource = dataSource };
        if (options.DataSource == ":memory:") csb.Mode = SqliteOpenMode.Memory;

        _connection = new SqliteConnection(csb.ToString());
        _connection.Open();
        SqliteSchema.Apply(_connection);
    }

    // ── Queue CRUD ────────────────────────────────────────────────────

    public async Task CreateQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO queues(name) VALUES ($name)
                ON CONFLICT(name) DO NOTHING;
                INSERT INTO sequence_counters(queue_name, next_sequence) VALUES ($name, 0)
                ON CONFLICT(queue_name) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$name", queueName);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM queues WHERE name = $name";
            cmd.Parameters.AddWithValue("$name", queueName);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
        _notify.TryRemove(queueName, out _);
    }

    public IReadOnlyCollection<string> ListQueueNames()
    {
        _gate.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM queues";
            using var rdr = cmd.ExecuteReader();
            var names = new List<string>();
            while (rdr.Read()) names.Add(rdr.GetString(0));
            return names;
        }
        finally { _gate.Release(); }
    }

    // ── Enqueue / dedup / sequence ────────────────────────────────────

    public async Task<StoredMessage> EnqueueAsync(
        string queueName,
        byte[] encodedMessage,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? scheduledEnqueueTime = null,
        string? sessionId = null,
        string? messageId = null,
        TimeSpan? duplicateDetectionWindow = null,
        int deliveryCount = 0,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var dedupEnabled = duplicateDetectionWindow is not null && !string.IsNullOrEmpty(messageId);

            // A scheduled time in the past has no meaning - fold it into "available immediately"
            // so the dequeue path doesn't need a "scheduled but already due" check.
            var effectiveSchedule = scheduledEnqueueTime is { } sched && sched > now ? scheduledEnqueueTime : null;

            // Queue-row creation, the dedup check, the sequence bump, the message INSERT, and the
            // dedup-history INSERT all run in ONE transaction. Otherwise a crash between the
            // message and dedup-history writes would persist the message without its dedup record,
            // and the sender's retry of the same MessageId would be accepted as new after restart -
            // defeating crash-safe dedup, which is the whole point of the persistent store.
            var (stored, wasDuplicate) = WithTransaction(tx =>
            {
                EnsureQueueRow(tx, queueName);

                // Dedup check happens before sequence allocation so duplicate sends don't burn ids.
                if (dedupEnabled)
                {
                    SweepDedupHistory(tx, queueName, now);
                    var existing = LookupDedupOriginal(tx, queueName, messageId!, now);
                    if (existing is not null) return (existing, true);
                }

                var seq = AllocateSequence(tx, queueName);

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO messages
                            (queue_name, sequence_number, enqueued_at, encoded_message,
                             delivery_count, expires_at, scheduled_enqueue_time, is_deferred, session_id)
                        VALUES
                            ($q, $seq, $enq, $body, $dc, $exp, $sched, 0, $sid)
                        """;
                    cmd.Parameters.AddWithValue("$q", queueName);
                    cmd.Parameters.AddWithValue("$dc", deliveryCount);
                    cmd.Parameters.AddWithValue("$seq", seq);
                    cmd.Parameters.AddWithValue("$enq", ToUnixMs(now));
                    cmd.Parameters.AddWithValue("$body", encodedMessage);
                    cmd.Parameters.AddWithValue("$exp", (object?)ToUnixMs(expiresAt) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$sched", (object?)ToUnixMs(effectiveSchedule) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                if (dedupEnabled)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO dedup_history(queue_name, message_id, original_sequence_number, expires_at)
                        VALUES ($q, $mid, $seq, $exp)
                        ON CONFLICT(queue_name, message_id) DO UPDATE SET
                            original_sequence_number = excluded.original_sequence_number,
                            expires_at = excluded.expires_at
                        """;
                    cmd.Parameters.AddWithValue("$q", queueName);
                    cmd.Parameters.AddWithValue("$mid", messageId);
                    cmd.Parameters.AddWithValue("$seq", seq);
                    cmd.Parameters.AddWithValue("$exp", ToUnixMs(now + duplicateDetectionWindow!.Value));
                    cmd.ExecuteNonQuery();
                }

                var message = new StoredMessage
                {
                    SequenceNumber = seq,
                    EnqueuedAt = now,
                    EncodedMessage = encodedMessage,
                    ExpiresAt = expiresAt,
                    ScheduledEnqueueTime = effectiveSchedule,
                    SessionId = sessionId,
                    DeliveryCount = deliveryCount,
                };
                return (message, false);
            });

            if (wasDuplicate)
            {
                return stored;
            }

            _enqueuedTotal.AddOrUpdate(queueName, 1, (_, v) => v + 1); // cumulative "new messages"

            // Signal the dequeue waiter, only when the message would actually be visible -
            // scheduled messages don't wake anyone (the activator does, later).
            if (effectiveSchedule is null)
            {
                NotifyAvailable(queueName);
            }

            return stored;
        }
        finally { _gate.Release(); }
    }

    private long AllocateSequence(SqliteTransaction tx, string queueName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE sequence_counters
            SET next_sequence = next_sequence + 1
            WHERE queue_name = $q
            RETURNING next_sequence
            """;
        cmd.Parameters.AddWithValue("$q", queueName);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull
            ? throw new InvalidOperationException($"Queue '{queueName}' does not exist.")
            : Convert.ToInt64(result);
    }

    private void EnsureQueueRow(SqliteTransaction tx, string queueName)
    {
        // Called inside the gate; idempotent - handles the "enqueue to a queue created
        // moments ago in a parallel session" edge case where the FK would otherwise fail.
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO queues(name) VALUES ($q) ON CONFLICT(name) DO NOTHING;
            INSERT INTO sequence_counters(queue_name, next_sequence) VALUES ($q, 0)
            ON CONFLICT(queue_name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.ExecuteNonQuery();
    }

    private void SweepDedupHistory(SqliteTransaction tx, string queueName, DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM dedup_history WHERE queue_name = $q AND expires_at <= $now";
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.Parameters.AddWithValue("$now", ToUnixMs(now));
        cmd.ExecuteNonQuery();
    }

    private StoredMessage? LookupDedupOriginal(SqliteTransaction tx, string queueName, string messageId, DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // Left join so a duplicate is still recognised (and dropped) even after the original
        // message has been settled/expired and removed from the messages table - the dedup
        // window, not the message's continued existence, defines a duplicate. When the original
        // row is gone we return a synthetic StoredMessage carrying the recorded sequence number.
        cmd.CommandText = """
            SELECT d.original_sequence_number,
                   m.enqueued_at, m.encoded_message, m.delivery_count,
                   m.expires_at, m.scheduled_enqueue_time, m.is_deferred, m.session_id
            FROM dedup_history d
            LEFT JOIN messages m
              ON m.queue_name = d.queue_name AND m.sequence_number = d.original_sequence_number
            WHERE d.queue_name = $q AND d.message_id = $mid AND d.expires_at > $now
            """;
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$now", ToUnixMs(now));
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;

        // If the original message row is gone, the joined columns are NULL - return a minimal
        // record so callers that only propagate the sequence number stay consistent.
        if (rdr.IsDBNull(2))
        {
            return new StoredMessage
            {
                SequenceNumber = rdr.GetInt64(0),
                EnqueuedAt = now,
                EncodedMessage = Array.Empty<byte>(),
            };
        }
        return ReadStoredMessage(rdr, sequenceColIndex: 0);
    }

    // ── Counting / peek / list ───────────────────────────────────────

    public async Task<long> CountAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE queue_name = $q";
            cmd.Parameters.AddWithValue("$q", queueName);
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
        finally { _gate.Release(); }
    }

    public (long Enqueued, long Completed) LifetimeCounters(string queueName) =>
        (_enqueuedTotal.GetValueOrDefault(queueName), _completedTotal.GetValueOrDefault(queueName));

    public IReadOnlyList<StoredMessage> Peek(string queueName, long fromSequenceNumber, int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<StoredMessage>();
        _gate.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT sequence_number, enqueued_at, encoded_message, delivery_count,
                       expires_at, scheduled_enqueue_time, is_deferred, session_id
                FROM messages
                WHERE queue_name = $q AND sequence_number >= $seq
                ORDER BY sequence_number
                LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$q", queueName);
            cmd.Parameters.AddWithValue("$seq", fromSequenceNumber);
            cmd.Parameters.AddWithValue("$lim", maxCount);
            using var rdr = cmd.ExecuteReader();
            var list = new List<StoredMessage>();
            while (rdr.Read()) list.Add(ReadStoredMessage(rdr, 0));
            return list;
        }
        finally { _gate.Release(); }
    }

    // ── Scheduled / TTL sweepers ─────────────────────────────────────

    public int ActivateScheduled(string queueName, DateTimeOffset now)
    {
        _gate.Wait();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE messages
                SET scheduled_enqueue_time = NULL
                WHERE queue_name = $q
                  AND scheduled_enqueue_time IS NOT NULL
                  AND scheduled_enqueue_time <= $now
                """;
            cmd.Parameters.AddWithValue("$q", queueName);
            cmd.Parameters.AddWithValue("$now", ToUnixMs(now));
            var affected = cmd.ExecuteNonQuery();
            if (affected > 0) NotifyAvailable(queueName);
            return affected;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryCancelScheduledAsync(string queueName, long sequenceNumber, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM messages
                WHERE queue_name = $q
                  AND sequence_number = $seq
                  AND scheduled_enqueue_time IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$q", queueName);
            cmd.Parameters.AddWithValue("$seq", sequenceNumber);
            return cmd.ExecuteNonQuery() > 0;
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<StoredMessage> ExpireMessages(string queueName, DateTimeOffset now)
    {
        _gate.Wait();
        try
        {
            // Find unlocked, expired, non-scheduled messages (locked ones are off-limits;
            // their holder gets to settle), then delete them. Returning the rows lets the
            // caller (TtlExpirationService) route them to DLQ if configured.
            var expired = new List<StoredMessage>();
            using (var select = _connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT m.sequence_number, m.enqueued_at, m.encoded_message, m.delivery_count,
                           m.expires_at, m.scheduled_enqueue_time, m.is_deferred, m.session_id
                    FROM messages m
                    WHERE m.queue_name = $q
                      AND m.expires_at IS NOT NULL
                      AND m.expires_at <= $now
                      AND (m.scheduled_enqueue_time IS NULL OR m.scheduled_enqueue_time <= $now)
                      AND NOT EXISTS (
                          SELECT 1 FROM locks l
                          WHERE l.queue_name = m.queue_name AND l.sequence_number = m.sequence_number
                      )
                    """;
                select.Parameters.AddWithValue("$q", queueName);
                select.Parameters.AddWithValue("$now", ToUnixMs(now));
                using var rdr = select.ExecuteReader();
                while (rdr.Read()) expired.Add(ReadStoredMessage(rdr, 0));
            }

            if (expired.Count == 0) return expired;

            // Bulk-delete by IN list. SQLite has a parameter limit (default 32766) so chunk.
            const int chunk = 500;
            for (var offset = 0; offset < expired.Count; offset += chunk)
            {
                var slice = expired.Skip(offset).Take(chunk).ToList();
                using var del = _connection.CreateCommand();
                var placeholders = string.Join(",", slice.Select((_, i) => $"$s{i}"));
                del.CommandText = $"DELETE FROM messages WHERE queue_name = $q AND sequence_number IN ({placeholders})";
                del.Parameters.AddWithValue("$q", queueName);
                for (var i = 0; i < slice.Count; i++)
                    del.Parameters.AddWithValue($"$s{i}", slice[i].SequenceNumber);
                del.ExecuteNonQuery();
            }

            return expired;
        }
        finally { _gate.Release(); }
    }

    // ── Dequeue / dispositions ───────────────────────────────────────

    public async Task<LockedMessage?> TryDequeueAsync(
        string queueName,
        TimeSpan lockDuration,
        string? associatedLinkName = null,
        CancellationToken cancellationToken = default)
    {
        // Long-poll: try-claim, wait on the notify channel (with poll-interval timeout), retry.
        while (!cancellationToken.IsCancellationRequested)
        {
            var locked = TryClaimNextNow(queueName, lockDuration, associatedLinkName, sessionId: null);
            if (locked is not null) return locked;

            // Wait for either a wake-up signal or the poll interval, whichever comes first.
            var notify = GetNotifyChannel(queueName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.DequeuePollInterval);
            try
            {
                await notify.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false);
                // Drain so the next iteration doesn't immediately re-trigger on stale signal.
                notify.Reader.TryRead(out _);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                // Otherwise it was just the poll-interval timeout; loop and retry the claim.
            }
        }
        return null;
    }

    private LockedMessage? TryClaimNextNow(string queueName, TimeSpan lockDuration, string? associatedLinkName, string? sessionId)
    {
        _gate.Wait();
        try
        {
            var now = _timeProvider.GetUtcNow();
            using var tx = _connection.BeginTransaction();

            // For a session receive, only the current lock holder may claim a message. Checking
            // inside the same transaction as the claim keeps sessions exclusive: a receiver whose
            // lock expired (and was handed to someone else) cannot pull from the session.
            if (sessionId is not null && !SessionLockHeldBy(tx, queueName, sessionId, associatedLinkName, now))
            {
                tx.Commit();
                return null;
            }

            // Find the lowest-seq message that's available (not deferred, not scheduled-future,
            // not currently locked, optionally matching a session). The session filter is used
            // by TryDequeueFromSessionAsync - passing null means "no session filter".
            using var select = _connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText = sessionId is null
                ? """
                  SELECT m.sequence_number, m.enqueued_at, m.encoded_message, m.delivery_count,
                         m.expires_at, m.scheduled_enqueue_time, m.is_deferred, m.session_id
                  FROM messages m
                  WHERE m.queue_name = $q
                    AND m.is_deferred = 0
                    AND (m.scheduled_enqueue_time IS NULL OR m.scheduled_enqueue_time <= $now)
                    AND NOT EXISTS (
                        SELECT 1 FROM locks l
                        WHERE l.queue_name = m.queue_name AND l.sequence_number = m.sequence_number
                    )
                  ORDER BY m.sequence_number
                  LIMIT 1
                  """
                : """
                  SELECT m.sequence_number, m.enqueued_at, m.encoded_message, m.delivery_count,
                         m.expires_at, m.scheduled_enqueue_time, m.is_deferred, m.session_id
                  FROM messages m
                  WHERE m.queue_name = $q
                    AND m.session_id = $sid
                    AND m.is_deferred = 0
                    AND (m.scheduled_enqueue_time IS NULL OR m.scheduled_enqueue_time <= $now)
                    AND NOT EXISTS (
                        SELECT 1 FROM locks l
                        WHERE l.queue_name = m.queue_name AND l.sequence_number = m.sequence_number
                    )
                  ORDER BY m.sequence_number
                  LIMIT 1
                  """;
            select.Parameters.AddWithValue("$q", queueName);
            select.Parameters.AddWithValue("$now", ToUnixMs(now));
            if (sessionId is not null) select.Parameters.AddWithValue("$sid", sessionId);

            StoredMessage? msg = null;
            using (var rdr = select.ExecuteReader())
            {
                if (rdr.Read()) msg = ReadStoredMessage(rdr, 0);
            }
            if (msg is null) { tx.Commit(); return null; }

            var lockToken = Guid.NewGuid();
            var lockedUntil = now + lockDuration;
            using (var insertLock = _connection.CreateCommand())
            {
                insertLock.Transaction = tx;
                insertLock.CommandText = """
                    INSERT INTO locks
                        (lock_token, queue_name, sequence_number, locked_until, associated_link, was_deferred, session_id)
                    VALUES
                        ($tok, $q, $seq, $until, $link, 0, $sid)
                    """;
                insertLock.Parameters.AddWithValue("$tok", lockToken.ToString("D"));
                insertLock.Parameters.AddWithValue("$q", queueName);
                insertLock.Parameters.AddWithValue("$seq", msg.SequenceNumber);
                insertLock.Parameters.AddWithValue("$until", ToUnixMs(lockedUntil));
                insertLock.Parameters.AddWithValue("$link", (object?)associatedLinkName ?? DBNull.Value);
                insertLock.Parameters.AddWithValue("$sid", (object?)msg.SessionId ?? DBNull.Value);
                insertLock.ExecuteNonQuery();
            }
            tx.Commit();

            return new LockedMessage { Message = msg, LockToken = lockToken, LockedUntil = lockedUntil };
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryCompleteAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return WithTransaction(tx =>
            {
                var entry = ReadLock(tx, lockToken);
                if (entry is null) return false;
                DeleteLock(tx, lockToken);
                DeleteMessage(tx, entry.QueueName, entry.SequenceNumber);
                _completedTotal.AddOrUpdate(entry.QueueName, 1, (_, v) => v + 1); // cumulative "completed"
                return true;
            });
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryAbandonAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var notify = false;
            var ok = WithTransaction(tx =>
            {
                var entry = ReadLock(tx, lockToken);
                if (entry is null) return false;
                DeleteLock(tx, lockToken);
                if (entry.WasDeferred)
                {
                    // Defer → received → abandoned: return to Deferred state, not Active.
                    SetDeferred(tx, entry.QueueName, entry.SequenceNumber, deferred: true);
                }
                else
                {
                    IncrementDeliveryCount(tx, entry.QueueName, entry.SequenceNumber);
                    notify = true;
                }
                return true;
            });
            if (notify) NotifyAvailable(queueName);
            return ok;
        }
        finally { _gate.Release(); }
    }

    public int ExpireLocks(string queueName, DateTimeOffset now)
    {
        _gate.Wait();
        try
        {
            // Collect first so we can decide per-row whether to bump delivery_count
            // (active locks) or restore deferred state (was_deferred locks).
            var expired = new List<LockRow>();
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT lock_token, queue_name, sequence_number, was_deferred, session_id
                    FROM locks
                    WHERE queue_name = $q AND locked_until <= $now
                    """;
                sel.Parameters.AddWithValue("$q", queueName);
                sel.Parameters.AddWithValue("$now", ToUnixMs(now));
                using var rdr = sel.ExecuteReader();
                while (rdr.Read())
                {
                    expired.Add(new LockRow(
                        rdr.GetString(0),
                        rdr.GetString(1),
                        rdr.GetInt64(2),
                        rdr.GetInt64(3) != 0,
                        rdr.IsDBNull(4) ? null : rdr.GetString(4)));
                }
            }

            if (expired.Count == 0) return 0;
            var notify = false;
            WithTransaction(tx =>
            {
                foreach (var row in expired)
                {
                    using var del = _connection.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM locks WHERE lock_token = $tok";
                    del.Parameters.AddWithValue("$tok", row.LockToken);
                    del.ExecuteNonQuery();

                    if (row.WasDeferred)
                    {
                        SetDeferred(tx, row.QueueName, row.SequenceNumber, deferred: true);
                    }
                    else
                    {
                        IncrementDeliveryCount(tx, row.QueueName, row.SequenceNumber);
                        notify = true;
                    }
                }
                return true;
            });
            if (notify) NotifyAvailable(queueName);
            return expired.Count;
        }
        finally { _gate.Release(); }
    }

    public async Task<DateTimeOffset?> TryRenewLockAsync(
        string queueName,
        Guid lockToken,
        TimeSpan lockDuration,
        string? requestingLinkName = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            var entry = ReadLock(tx, lockToken);
            if (entry is null) { tx.Commit(); return null; }

            // Link-affinity enforcement: a renew issued from a different link returns null
            // (the disposition path surfaces this as "Gone"). Matches Service Bus.
            if (entry.AssociatedLink is not null
                && requestingLinkName is not null
                && !string.Equals(entry.AssociatedLink, requestingLinkName, StringComparison.Ordinal))
            {
                tx.Commit();
                return null;
            }

            var newUntil = _timeProvider.GetUtcNow() + lockDuration;
            using (var upd = _connection.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE locks SET locked_until = $until WHERE lock_token = $tok";
                upd.Parameters.AddWithValue("$until", ToUnixMs(newUntil));
                upd.Parameters.AddWithValue("$tok", lockToken.ToString("D"));
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            return newUntil;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoredMessage?> TryRemoveLockedAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredMessage? removed = null;
            WithTransaction(tx =>
            {
                var entry = ReadLock(tx, lockToken);
                if (entry is null) return false;
                using var sel = _connection.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = """
                    SELECT sequence_number, enqueued_at, encoded_message, delivery_count,
                           expires_at, scheduled_enqueue_time, is_deferred, session_id
                    FROM messages
                    WHERE queue_name = $q AND sequence_number = $seq
                    """;
                sel.Parameters.AddWithValue("$q", entry.QueueName);
                sel.Parameters.AddWithValue("$seq", entry.SequenceNumber);
                using var rdr = sel.ExecuteReader();
                if (rdr.Read()) removed = ReadStoredMessage(rdr, 0);
                rdr.Close();
                DeleteLock(tx, lockToken);
                DeleteMessage(tx, entry.QueueName, entry.SequenceNumber);
                return true;
            });
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryDeferAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return WithTransaction(tx =>
            {
                var entry = ReadLock(tx, lockToken);
                if (entry is null) return false;
                DeleteLock(tx, lockToken);
                SetDeferred(tx, entry.QueueName, entry.SequenceNumber, deferred: true);
                // Note: no NotifyAvailable - deferred messages aren't available to the normal pool.
                return true;
            });
        }
        finally { _gate.Release(); }
    }

    public async Task<LockedMessage?> TryReceiveDeferredAsync(
        string queueName,
        long sequenceNumber,
        TimeSpan lockDuration,
        string? associatedLinkName = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            using var sel = _connection.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT sequence_number, enqueued_at, encoded_message, delivery_count,
                       expires_at, scheduled_enqueue_time, is_deferred, session_id
                FROM messages
                WHERE queue_name = $q AND sequence_number = $seq AND is_deferred = 1
                """;
            sel.Parameters.AddWithValue("$q", queueName);
            sel.Parameters.AddWithValue("$seq", sequenceNumber);
            StoredMessage? msg = null;
            using (var rdr = sel.ExecuteReader())
            {
                if (rdr.Read()) msg = ReadStoredMessage(rdr, 0);
            }
            if (msg is null) { tx.Commit(); return null; }

            // Clear the deferred flag while we hold it under a fresh lock with WasDeferred=true,
            // so a subsequent abandon brings it back to Deferred (not Active).
            SetDeferred(tx, queueName, sequenceNumber, deferred: false);

            var lockToken = Guid.NewGuid();
            var lockedUntil = _timeProvider.GetUtcNow() + lockDuration;
            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO locks
                        (lock_token, queue_name, sequence_number, locked_until, associated_link, was_deferred, session_id)
                    VALUES
                        ($tok, $q, $seq, $until, $link, 1, $sid)
                    """;
                ins.Parameters.AddWithValue("$tok", lockToken.ToString("D"));
                ins.Parameters.AddWithValue("$q", queueName);
                ins.Parameters.AddWithValue("$seq", sequenceNumber);
                ins.Parameters.AddWithValue("$until", ToUnixMs(lockedUntil));
                ins.Parameters.AddWithValue("$link", (object?)associatedLinkName ?? DBNull.Value);
                ins.Parameters.AddWithValue("$sid", (object?)msg.SessionId ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }
            tx.Commit();

            // The returned message reflects the cleared deferred flag.
            return new LockedMessage
            {
                Message = msg with { IsDeferred = false },
                LockToken = lockToken,
                LockedUntil = lockedUntil,
            };
        }
        finally { _gate.Release(); }
    }

    // ── Sessions ───────────────────────────────────────────────

    public async Task<SessionLock?> TryAcceptSessionAsync(
        string queueName,
        string sessionId,
        TimeSpan lockDuration,
        string? linkName = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            using var tx = _connection.BeginTransaction();

            using (var existing = _connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT locked_until FROM session_locks WHERE queue_name = $q AND session_id = $sid";
                existing.Parameters.AddWithValue("$q", queueName);
                existing.Parameters.AddWithValue("$sid", sessionId);
                var result = existing.ExecuteScalar();
                if (result is not null and not DBNull && Convert.ToInt64(result) > ToUnixMs(now))
                {
                    tx.Commit();
                    return null;
                }
            }

            var lockedUntil = now + lockDuration;
            using (var upsert = _connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO session_locks(queue_name, session_id, locked_until, link_name)
                    VALUES ($q, $sid, $until, $link)
                    ON CONFLICT(queue_name, session_id) DO UPDATE SET
                        locked_until = excluded.locked_until,
                        link_name    = excluded.link_name
                    """;
                upsert.Parameters.AddWithValue("$q", queueName);
                upsert.Parameters.AddWithValue("$sid", sessionId);
                upsert.Parameters.AddWithValue("$until", ToUnixMs(lockedUntil));
                upsert.Parameters.AddWithValue("$link", (object?)linkName ?? DBNull.Value);
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
            return new SessionLock { SessionId = sessionId, LockedUntil = lockedUntil, LinkName = linkName };
        }
        finally { _gate.Release(); }
    }

    public async Task<SessionLock?> TryAcceptNextSessionAsync(
        string queueName,
        TimeSpan lockDuration,
        string? linkName = null,
        CancellationToken cancellationToken = default)
    {
        // Two-phase under the same gate hold: pick the lowest-seq unlocked session that has
        // available messages, then claim it. ORDER BY MIN(sequence_number) preserves FIFO across
        // sessions so newly-arrived sessions don't starve older ones.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            string? candidate;
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT m.session_id
                    FROM messages m
                    LEFT JOIN session_locks sl
                      ON sl.queue_name = m.queue_name AND sl.session_id = m.session_id
                    WHERE m.queue_name = $q
                      AND m.session_id IS NOT NULL
                      AND m.is_deferred = 0
                      AND (m.scheduled_enqueue_time IS NULL OR m.scheduled_enqueue_time <= $now)
                      AND (sl.locked_until IS NULL OR sl.locked_until <= $now)
                    GROUP BY m.session_id
                    ORDER BY MIN(m.sequence_number)
                    LIMIT 1
                    """;
                sel.Parameters.AddWithValue("$q", queueName);
                sel.Parameters.AddWithValue("$now", ToUnixMs(now));
                var result = sel.ExecuteScalar();
                candidate = result as string;
            }
            if (candidate is null) return null;

            // Claim it inline (still under the gate) so the candidate can't be sniped between
            // the SELECT and the lock acquisition.
            var lockedUntil = now + lockDuration;
            using (var upsert = _connection.CreateCommand())
            {
                upsert.CommandText = """
                    INSERT INTO session_locks(queue_name, session_id, locked_until, link_name)
                    VALUES ($q, $sid, $until, $link)
                    ON CONFLICT(queue_name, session_id) DO UPDATE SET
                        locked_until = excluded.locked_until,
                        link_name    = excluded.link_name
                    """;
                upsert.Parameters.AddWithValue("$q", queueName);
                upsert.Parameters.AddWithValue("$sid", candidate);
                upsert.Parameters.AddWithValue("$until", ToUnixMs(lockedUntil));
                upsert.Parameters.AddWithValue("$link", (object?)linkName ?? DBNull.Value);
                upsert.ExecuteNonQuery();
            }
            return new SessionLock { SessionId = candidate, LockedUntil = lockedUntil, LinkName = linkName };
        }
        finally { _gate.Release(); }
    }

    public async Task<LockedMessage?> TryDequeueFromSessionAsync(
        string queueName,
        string sessionId,
        TimeSpan messageLockDuration,
        string? linkName = null,
        CancellationToken cancellationToken = default)
    {
        // Same long-poll shape as TryDequeueAsync but with the session filter applied.
        while (!cancellationToken.IsCancellationRequested)
        {
            // If this receiver no longer holds the session lock, return null WITHOUT the poll
            // cancellation so the AMQP session receiver stops its pump (lock-lost) instead of
            // spinning against a session another receiver now owns.
            if (!IsSessionLockHeld(queueName, sessionId, linkName)) return null;

            var locked = TryClaimNextNow(queueName, messageLockDuration, linkName, sessionId);
            if (locked is not null) return locked;
            var notify = GetNotifyChannel(queueName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.DequeuePollInterval);
            try
            {
                await notify.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false);
                notify.Reader.TryRead(out _);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) return null;
            }
        }
        return null;
    }

    /// <summary>
    /// True when the session is locked, unexpired, and (if the lock records a holder link) held
    /// by <paramref name="linkName"/>. Evaluated inside the caller's transaction.
    /// </summary>
    private bool SessionLockHeldBy(SqliteTransaction tx, string queueName, string sessionId, string? linkName, DateTimeOffset now)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT locked_until, link_name FROM session_locks WHERE queue_name = $q AND session_id = $sid";
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return false;
        if (FromUnixMs(rdr.GetInt64(0)) <= now) return false;
        var holder = rdr.IsDBNull(1) ? null : rdr.GetString(1);
        return holder is null || linkName is null || string.Equals(holder, linkName, StringComparison.Ordinal);
    }

    private bool IsSessionLockHeld(string queueName, string sessionId, string? linkName)
    {
        _gate.Wait();
        try
        {
            using var tx = _connection.BeginTransaction();
            var held = SessionLockHeldBy(tx, queueName, sessionId, linkName, _timeProvider.GetUtcNow());
            tx.Commit();
            return held;
        }
        finally { _gate.Release(); }
    }

    public async Task<DateTimeOffset?> TryRenewSessionLockAsync(
        string queueName,
        string sessionId,
        TimeSpan lockDuration,
        string? requestingLinkName = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            string? link = null;
            using (var sel = _connection.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT link_name FROM session_locks WHERE queue_name = $q AND session_id = $sid";
                sel.Parameters.AddWithValue("$q", queueName);
                sel.Parameters.AddWithValue("$sid", sessionId);
                using var rdr = sel.ExecuteReader();
                if (!rdr.Read()) { tx.Commit(); return null; }
                link = rdr.IsDBNull(0) ? null : rdr.GetString(0);
            }
            if (link is not null && requestingLinkName is not null
                && !string.Equals(link, requestingLinkName, StringComparison.Ordinal))
            {
                tx.Commit();
                return null;
            }

            var newUntil = _timeProvider.GetUtcNow() + lockDuration;
            using (var upd = _connection.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE session_locks SET locked_until = $until WHERE queue_name = $q AND session_id = $sid";
                upd.Parameters.AddWithValue("$until", ToUnixMs(newUntil));
                upd.Parameters.AddWithValue("$q", queueName);
                upd.Parameters.AddWithValue("$sid", sessionId);
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            return newUntil;
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseSessionAsync(string queueName, string sessionId, string? expectedLinkName = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            // Release only if we still hold it: when expectedLinkName is given, require the stored
            // holder to match (or be NULL) so a stale receiver's late detach can't drop the lock a
            // new receiver already acquired for the same session.
            cmd.CommandText = """
                DELETE FROM session_locks
                WHERE queue_name = $q AND session_id = $sid
                  AND ($link IS NULL OR link_name IS NULL OR link_name = $link)
                """;
            cmd.Parameters.AddWithValue("$q", queueName);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$link", (object?)expectedLinkName ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public async Task SetSessionStateAsync(string queueName, string sessionId, byte[]? state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_state(queue_name, session_id, state)
                VALUES ($q, $sid, $state)
                ON CONFLICT(queue_name, session_id) DO UPDATE SET state = excluded.state
                """;
            cmd.Parameters.AddWithValue("$q", queueName);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]?> GetSessionStateAsync(string queueName, string sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT state FROM session_state WHERE queue_name = $q AND session_id = $sid";
            cmd.Parameters.AddWithValue("$q", queueName);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            var result = cmd.ExecuteScalar();
            return result is byte[] bytes ? bytes : null;
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<string> ListSessions(string queueName)
    {
        _gate.Wait();
        try
        {
            // A session is "live" if it has any non-deferred message OR a stored state - same
            // contract as the in-memory store, used by the $management get-message-sessions op.
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT session_id FROM (
                    SELECT session_id FROM messages WHERE queue_name = $q AND session_id IS NOT NULL
                    UNION
                    SELECT session_id FROM session_state WHERE queue_name = $q
                )
                """;
            cmd.Parameters.AddWithValue("$q", queueName);
            using var rdr = cmd.ExecuteReader();
            var ids = new List<string>();
            while (rdr.Read()) if (!rdr.IsDBNull(0)) ids.Add(rdr.GetString(0));
            return ids;
        }
        finally { _gate.Release(); }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private Channel<bool> GetNotifyChannel(string queueName) =>
        _notify.GetOrAdd(queueName, _ => Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }));

    private void NotifyAvailable(string queueName) =>
        GetNotifyChannel(queueName).Writer.TryWrite(true);

    private static long? ToUnixMs(DateTimeOffset? dto) =>
        dto?.ToUnixTimeMilliseconds();
    private static long ToUnixMs(DateTimeOffset dto) =>
        dto.ToUnixTimeMilliseconds();
    private static DateTimeOffset? FromUnixMs(long? ms) =>
        ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
    private static DateTimeOffset FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>
    /// Reads a <see cref="StoredMessage"/> from a row whose columns start at
    /// <paramref name="sequenceColIndex"/> in the order: seq, enqueued_at, body, delivery_count,
    /// expires_at, scheduled_enqueue_time, is_deferred, session_id.
    /// </summary>
    private static StoredMessage ReadStoredMessage(SqliteDataReader rdr, int sequenceColIndex)
    {
        var i = sequenceColIndex;
        return new StoredMessage
        {
            SequenceNumber = rdr.GetInt64(i + 0),
            EnqueuedAt = FromUnixMs(rdr.GetInt64(i + 1)),
            EncodedMessage = (byte[])rdr.GetValue(i + 2),
            DeliveryCount = rdr.GetInt32(i + 3),
            ExpiresAt = rdr.IsDBNull(i + 4) ? null : FromUnixMs(rdr.GetInt64(i + 4)),
            ScheduledEnqueueTime = rdr.IsDBNull(i + 5) ? null : FromUnixMs(rdr.GetInt64(i + 5)),
            IsDeferred = rdr.GetInt64(i + 6) != 0,
            SessionId = rdr.IsDBNull(i + 7) ? null : rdr.GetString(i + 7),
        };
    }

    private LockEntryRow? ReadLock(SqliteTransaction tx, Guid lockToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT queue_name, sequence_number, locked_until, associated_link, was_deferred, session_id
            FROM locks WHERE lock_token = $tok
            """;
        cmd.Parameters.AddWithValue("$tok", lockToken.ToString("D"));
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        return new LockEntryRow(
            rdr.GetString(0),
            rdr.GetInt64(1),
            FromUnixMs(rdr.GetInt64(2)),
            rdr.IsDBNull(3) ? null : rdr.GetString(3),
            rdr.GetInt64(4) != 0,
            rdr.IsDBNull(5) ? null : rdr.GetString(5));
    }

    private void DeleteLock(SqliteTransaction tx, Guid lockToken)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM locks WHERE lock_token = $tok";
        cmd.Parameters.AddWithValue("$tok", lockToken.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    private void DeleteMessage(SqliteTransaction tx, string queueName, long seq)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM messages WHERE queue_name = $q AND sequence_number = $seq";
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    private void SetDeferred(SqliteTransaction tx, string queueName, long seq, bool deferred)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE messages SET is_deferred = $d WHERE queue_name = $q AND sequence_number = $seq";
        cmd.Parameters.AddWithValue("$d", deferred ? 1 : 0);
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    private void IncrementDeliveryCount(SqliteTransaction tx, string queueName, long seq)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE messages SET delivery_count = delivery_count + 1 WHERE queue_name = $q AND sequence_number = $seq";
        cmd.Parameters.AddWithValue("$q", queueName);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    private T WithTransaction<T>(Func<SqliteTransaction, T> body)
    {
        using var tx = _connection.BeginTransaction();
        var result = body(tx);
        tx.Commit();
        return result;
    }

    private sealed record LockEntryRow(
        string QueueName,
        long SequenceNumber,
        DateTimeOffset LockedUntil,
        string? AssociatedLink,
        bool WasDeferred,
        string? SessionId);

    private sealed record LockRow(
        string LockToken,
        string QueueName,
        long SequenceNumber,
        bool WasDeferred,
        string? SessionId);

    public ValueTask DisposeAsync()
    {
        _connection.Close();
        _connection.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
