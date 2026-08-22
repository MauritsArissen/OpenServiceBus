using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.InMemoryStorage;

/// <summary>
/// In-memory message store with peek-lock semantics.
///
/// Per queue:
///  - <c>messages</c>: every accepted message keyed by sequence number (active OR locked, but not yet completed).
///  - <c>available</c>: a <see cref="Channel{T}"/> of sequence numbers ready for delivery (FIFO).
///    Enqueue writes, dequeue reads, abandon writes back.
///  - <c>locks</c>: outstanding peek-locks keyed by lock token.
/// </summary>
public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, QueueState> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TopicDedupState> _topicDedup = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryMessageStore() : this(TimeProvider.System) { }

    public InMemoryMessageStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public IReadOnlyCollection<string> ListQueueNames() => _queues.Keys.ToArray();

    public Task CreateQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        _queues.TryAdd(queueName, new QueueState());
        return Task.CompletedTask;
    }

    public Task DeleteQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        if (_queues.TryRemove(queueName, out var state))
        {
            state.Available.Writer.TryComplete();
        }
        return Task.CompletedTask;
    }

    public Task<long> PurgeAsync(string queueName, CancellationToken cancellationToken = default)
    {
        if (!_queues.TryGetValue(queueName, out var state))
        {
            return Task.FromResult(0L);
        }

        long purged;
        lock (state.Sync)
        {
            purged = state.Messages.Count;
            state.Messages.Clear();
            state.Locks.Clear();
            state.SeenMessageIds.Clear();
            state.OriginalsByMessageId.Clear();
            while (state.Available.Reader.TryRead(out _))
            {
            }
            foreach (var session in state.Sessions.Values)
            {
                while (session.Available.Reader.TryRead(out _))
                {
                }
                session.State = null;
            }
        }
        return Task.FromResult(purged);
    }

    // Topic-level dedup mirrors the queue path's semantics (atomic check, lazy sweep,
    // TimeProvider-driven) but is keyed on the topic name - topics store no messages, so
    // there is no original StoredMessage to return, only the drop decision. A repeat
    // slides the window forward, matching the SQLite queue implementation.
    public Task<bool> CheckTopicDuplicateAsync(string topicName, string messageId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(messageId)) return Task.FromResult(false);
        var state = _topicDedup.GetOrAdd(topicName, _ => new TopicDedupState());
        var now = _timeProvider.GetUtcNow();
        lock (state.Sync)
        {
            List<string>? expired = null;
            foreach (var (key, deadline) in state.Seen)
            {
                if (deadline <= now) (expired ??= []).Add(key);
            }
            if (expired is not null)
            {
                foreach (var key in expired) state.Seen.Remove(key);
            }

            var duplicate = state.Seen.TryGetValue(messageId, out var existingDeadline) && existingDeadline > now;
            state.Seen[messageId] = now + window;
            return Task.FromResult(duplicate);
        }
    }

    public Task ClearTopicDedupHistoryAsync(string topicName, CancellationToken cancellationToken = default)
    {
        _topicDedup.TryRemove(topicName, out _);
        return Task.CompletedTask;
    }

    public Task<StoredMessage> EnqueueAsync(
        string queueName,
        byte[] encodedMessage,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? scheduledEnqueueTime = null,
        string? sessionId = null,
        string? messageId = null,
        TimeSpan? duplicateDetectionWindow = null,
        int deliveryCount = 0,
        long? enqueuedSequenceNumber = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        var now = _timeProvider.GetUtcNow();

        // The dedup check and the enqueue must be atomic relative to each other, otherwise two
        // concurrent sends of the same MessageId (exactly the retry case dedup exists for) can
        // both pass the check and both land. Hold the per-queue lock for the whole body; it is
        // all synchronous (no await), so this cannot block the thread pool for long.
        StoredMessage message;
        lock (state.Sync)
        {
            // Silent-drop duplicate sends. Azure SB does not surface the dup to the sender -
            // the SDK gets an "accepted" disposition either way. We return the *original*
            // StoredMessage so callers that propagate sequence numbers stay consistent.
            if (duplicateDetectionWindow is not null && !string.IsNullOrEmpty(messageId))
            {
                // Lazy sweep of expired entries on each check; cheap because the map is small.
                // Drop both parallel maps together so OriginalsByMessageId can't grow unbounded.
                foreach (var (key, expiresAtUtc) in state.SeenMessageIds)
                {
                    if (expiresAtUtc <= now)
                    {
                        state.SeenMessageIds.TryRemove(key, out _);
                        state.OriginalsByMessageId.TryRemove(key, out _);
                    }
                }
                if (state.SeenMessageIds.TryGetValue(messageId, out var existingDeadline) && existingDeadline > now
                    && state.OriginalsByMessageId.TryGetValue(messageId, out var original))
                {
                    return Task.FromResult(original);
                }
            }

            var seq = Interlocked.Increment(ref state.NextSequenceNumber);
            // A scheduled time in the past is meaningless - treat it as available immediately.
            var effectiveSchedule = scheduledEnqueueTime is { } sched && sched > now ? scheduledEnqueueTime : null;
            message = new StoredMessage
            {
                SequenceNumber = seq,
                EnqueuedSequenceNumber = enqueuedSequenceNumber ?? seq,
                EnqueuedAt = now,
                EncodedMessage = encodedMessage,
                ExpiresAt = expiresAt,
                ScheduledEnqueueTime = effectiveSchedule,
                SessionId = sessionId,
                DeliveryCount = deliveryCount,
            };

            state.Messages[seq] = message;
            Interlocked.Increment(ref state.EnqueuedTotal); // cumulative "new messages" counter
            if (duplicateDetectionWindow is not null && !string.IsNullOrEmpty(messageId))
            {
                state.SeenMessageIds[messageId] = now + duplicateDetectionWindow.Value;
                state.OriginalsByMessageId[messageId] = message;
            }
            if (effectiveSchedule is null)
            {
                if (sessionId is not null)
                {
                    // Session messages go to the per-session channel; only a session-locked receiver
                    // can read them, and ordering inside a session is preserved.
                    var session = state.GetOrAddSession(sessionId);
                    session.Available.Writer.TryWrite(seq);
                }
                else if (!state.Available.Writer.TryWrite(seq))
                {
                    throw new InvalidOperationException($"Queue '{queueName}' is closed.");
                }
            }
            // else: scheduled - waits in Messages until ActivateScheduled promotes it.
        }

        return Task.FromResult(message);
    }

    public Task<long> AllocateSequenceNumberAsync(string entityName, CancellationToken cancellationToken = default)
    {
        var state = _queues.GetOrAdd(entityName, _ => new QueueState());
        return Task.FromResult(Interlocked.Increment(ref state.NextSequenceNumber));
    }

    public int ActivateScheduled(string queueName, DateTimeOffset now)
    {
        if (!_queues.TryGetValue(queueName, out var state)) return 0;

        var activated = 0;
        foreach (var (seq, msg) in state.Messages)
        {
            if (msg.ScheduledEnqueueTime is null) continue;       // not scheduled
            if (msg.ScheduledEnqueueTime > now) continue;          // not yet
            // Clear the scheduled marker atomically; only the winner of TryUpdate pushes to Available.
            var activated_ = msg with { ScheduledEnqueueTime = null };
            if (state.Messages.TryUpdate(seq, activated_, msg))
            {
                // Mirror the enqueue path: session messages activate into their per-session
                // channel, otherwise a session receiver would never see them.
                if (msg.SessionId is not null)
                {
                    var session = state.GetOrAddSession(msg.SessionId);
                    session.Available.Writer.TryWrite(seq);
                }
                else
                {
                    state.Available.Writer.TryWrite(seq);
                }
                activated++;
            }
        }
        return activated;
    }

    public Task<bool> TryCancelScheduledAsync(
        string queueName,
        long sequenceNumber,
        CancellationToken cancellationToken = default)
    {
        if (!_queues.TryGetValue(queueName, out var state))
        {
            return Task.FromResult(false);
        }
        if (!state.Messages.TryGetValue(sequenceNumber, out var msg))
        {
            return Task.FromResult(false);
        }
        if (msg.ScheduledEnqueueTime is null)
        {
            // Already activated - caller should use normal disposition / lock-cancel paths.
            return Task.FromResult(false);
        }
        // TryRemove with comparison so we don't race with ActivateScheduled.
        return Task.FromResult(((ICollection<KeyValuePair<long, StoredMessage>>)state.Messages)
            .Remove(new KeyValuePair<long, StoredMessage>(sequenceNumber, msg)));
    }

    public IReadOnlyList<StoredMessage> ExpireMessages(string queueName, DateTimeOffset now)
    {
        if (!_queues.TryGetValue(queueName, out var state)) return Array.Empty<StoredMessage>();

        // Build a set of currently-locked sequence numbers - locked messages stay alive
        // even if they cross their TTL deadline; the lock holder gets the chance to settle them.
        var expired = new List<StoredMessage>();
        // Hold the queue lock so a message being claimed by a concurrent dequeue (which inserts
        // its lock entry under the same lock) is never removed here between the claim and the
        // lock insert - otherwise a message could be both delivered and expired.
        lock (state.Sync)
        {
            var locked = new HashSet<long>();
            foreach (var entry in state.Locks.Values) locked.Add(entry.SequenceNumber);

            foreach (var (seq, msg) in state.Messages)
            {
                if (!msg.IsExpired(now)) continue;
                if (locked.Contains(seq)) continue;
                if (state.Messages.TryRemove(seq, out var removed))
                {
                    expired.Add(removed);
                }
            }
        }
        return expired;
    }

    public Task<long> CountAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        return Task.FromResult((long)state.Messages.Count);
    }

    public long GetSizeInBytes(string queueName)
    {
        if (!_queues.TryGetValue(queueName, out var state)) return 0;
        long total = 0;
        foreach (var message in state.Messages.Values) total += message.EncodedMessage.Length;
        return total;
    }

    public (long Enqueued, long Completed) LifetimeCounters(string queueName)
    {
        if (!_queues.TryGetValue(queueName, out var state)) return (0, 0);
        return (Interlocked.Read(ref state.EnqueuedTotal), Interlocked.Read(ref state.CompletedTotal));
    }

    public async Task<LockedMessage?> TryDequeueAsync(
        string queueName,
        TimeSpan lockDuration,
        string? associatedLinkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);

        long seq;
        try
        {
            seq = await state.Available.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }

        // Claim the message and place the lock atomically w.r.t. the TTL sweeper. Without the
        // lock, ExpireMessages could remove this sequence between the lookup and the lock insert,
        // handing the same message to both the receiver and the DLQ.
        lock (state.Sync)
        {
            if (state.Messages.TryGetValue(seq, out var stored))
            {
                var lockToken = Guid.NewGuid();
                var lockedUntil = _timeProvider.GetUtcNow() + lockDuration;
                state.Locks[lockToken] = new LockEntry
                {
                    SequenceNumber = seq,
                    LockedUntil = lockedUntil,
                    AssociatedLink = associatedLinkName,
                };
                return new LockedMessage
                {
                    Message = stored,
                    LockToken = lockToken,
                    LockedUntil = lockedUntil,
                };
            }
        }

        // The message was completed/deleted/expired out-of-band - skip and try the next.
        return await TryDequeueAsync(queueName, lockDuration, associatedLinkName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claim a lock entry for settling, refusing expired locks: real Service Bus fails the
    /// settle at the lock deadline, not at the next sweeper pass. An expired entry is
    /// reclaimed on the spot (same transition the sweeper applies), so the message is
    /// available for redelivery immediately after the refused settle.
    /// </summary>
    private bool TryTakeLiveLock(QueueState state, Guid lockToken, out LockEntry entry)
    {
        lock (state.Sync)
        {
            if (!state.Locks.TryGetValue(lockToken, out entry!)) return false;
            if (entry.LockedUntil <= _timeProvider.GetUtcNow())
            {
                if (state.Locks.TryRemove(lockToken, out _))
                {
                    if (entry.WasDeferred)
                    {
                        if (state.Messages.TryGetValue(entry.SequenceNumber, out var msg))
                        {
                            state.Messages[entry.SequenceNumber] = msg with { IsDeferred = true };
                        }
                    }
                    else
                    {
                        ReturnToAvailableWithIncrementedDeliveryCount(state, entry.SequenceNumber, entry.SessionId);
                    }
                }
                return false;
            }
            state.Locks.TryRemove(lockToken, out _);
            return true;
        }
    }

    public Task<bool> TryCompleteAsync(
        string queueName,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!TryTakeLiveLock(state, lockToken, out var entry))
        {
            return Task.FromResult(false);
        }
        state.Messages.TryRemove(entry.SequenceNumber, out _);
        Interlocked.Increment(ref state.CompletedTotal); // cumulative "completed" counter
        return Task.FromResult(true);
    }

    public Task<bool> TryAbandonAsync(
        string queueName,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!TryTakeLiveLock(state, lockToken, out var entry))
        {
            return Task.FromResult(false);
        }

        if (entry.WasDeferred)
        {
            // Message was deferred before lock - abandon returns it to Deferred state, not Active.
            if (state.Messages.TryGetValue(entry.SequenceNumber, out var msg))
            {
                state.Messages[entry.SequenceNumber] = msg with { IsDeferred = true };
            }
            return Task.FromResult(true);
        }

        ReturnToAvailableWithIncrementedDeliveryCount(state, entry.SequenceNumber, entry.SessionId);
        return Task.FromResult(true);
    }

    public int ExpireLocks(string queueName, DateTimeOffset now)
    {
        if (!_queues.TryGetValue(queueName, out var state)) return 0;

        var expired = 0;
        // Lock so a renewal (which mutates LockedUntil in place) can't slip between reading the
        // deadline here and removing the token - otherwise a just-renewed lock could be revoked.
        lock (state.Sync)
        {
            foreach (var (token, entry) in state.Locks)
            {
                if (entry.LockedUntil > now) continue;
                if (!state.Locks.TryRemove(token, out _)) continue;

                if (entry.WasDeferred)
                {
                    // Deferred-then-locked messages return to Deferred on expiry, not to Active.
                    if (state.Messages.TryGetValue(entry.SequenceNumber, out var msg))
                    {
                        state.Messages[entry.SequenceNumber] = msg with { IsDeferred = true };
                    }
                }
                else
                {
                    ReturnToAvailableWithIncrementedDeliveryCount(state, entry.SequenceNumber, entry.SessionId);
                }
                expired++;
            }
        }
        return expired;
    }

    /// <summary>
    /// Bump <see cref="StoredMessage.DeliveryCount"/> and re-queue. Because <c>StoredMessage</c>
    /// is immutable we write a new record with <c>DeliveryCount + 1</c>; the next delivery attempt
    /// will stamp it onto the AMQP header. Session-bound messages go back to the per-session
    /// channel so only the session-locked receiver can re-receive them.
    /// </summary>
    private static void ReturnToAvailableWithIncrementedDeliveryCount(QueueState state, long sequenceNumber, string? sessionId)
    {
        if (state.Messages.TryGetValue(sequenceNumber, out var existing))
        {
            state.Messages[sequenceNumber] = existing with { DeliveryCount = existing.DeliveryCount + 1 };
        }
        if (sessionId is not null && state.Sessions.TryGetValue(sessionId, out var session))
        {
            session.Available.Writer.TryWrite(sequenceNumber);
        }
        else
        {
            state.Available.Writer.TryWrite(sequenceNumber);
        }
    }

    public Task<DateTimeOffset?> TryRenewLockAsync(
        string queueName,
        Guid lockToken,
        TimeSpan lockDuration,
        string? requestingLinkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        // Lock so the renewal is atomic against ExpireLocks: it must not renew a token the
        // sweeper is concurrently revoking, and the sweeper must not revoke one just renewed.
        lock (state.Sync)
        {
            // An expired lock cannot be revived by a late renew - the deadline passing IS the
            // loss, whether or not the sweeper has reclaimed the entry yet.
            if (!state.Locks.TryGetValue(lockToken, out var entry)
                || entry.LockedUntil <= _timeProvider.GetUtcNow())
            {
                return Task.FromResult<DateTimeOffset?>(null);
            }

            // Link-affinity check: if the lock was taken from a specific link, only that link
            // can renew it. Matches Service Bus's lock-link scoping - a sneaky cross-link renew
            // attempt returns Gone.
            if (entry.AssociatedLink is not null
                && requestingLinkName is not null
                && !string.Equals(entry.AssociatedLink, requestingLinkName, StringComparison.Ordinal))
            {
                return Task.FromResult<DateTimeOffset?>(null);
            }

            var newUntil = _timeProvider.GetUtcNow() + lockDuration;
            entry.LockedUntil = newUntil;
            return Task.FromResult<DateTimeOffset?>(newUntil);
        }
    }

    public Task<StoredMessage?> TryRemoveLockedAsync(
        string queueName,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!TryTakeLiveLock(state, lockToken, out var entry))
        {
            return Task.FromResult<StoredMessage?>(null);
        }
        state.Messages.TryRemove(entry.SequenceNumber, out var msg);
        return Task.FromResult<StoredMessage?>(msg);
    }

    public Task<StoredMessage?> TryGetLockedAsync(string queueName, Guid lockToken, CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        lock (state.Sync)
        {
            if (state.Locks.TryGetValue(lockToken, out var entry)
                && entry.LockedUntil > _timeProvider.GetUtcNow()
                && state.Messages.TryGetValue(entry.SequenceNumber, out var msg))
            {
                return Task.FromResult<StoredMessage?>(msg);
            }
        }
        return Task.FromResult<StoredMessage?>(null);
    }

    public Task<bool> TryUpdateLockedPayloadAsync(string queueName, Guid lockToken, byte[] encodedMessage, CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        lock (state.Sync)
        {
            if (!state.Locks.TryGetValue(lockToken, out var entry)
                || entry.LockedUntil <= _timeProvider.GetUtcNow()
                || !state.Messages.TryGetValue(entry.SequenceNumber, out var msg))
            {
                return Task.FromResult(false);
            }
            state.Messages[entry.SequenceNumber] = msg with { EncodedMessage = encodedMessage };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryDeferAsync(
        string queueName,
        Guid lockToken,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!TryTakeLiveLock(state, lockToken, out var entry))
        {
            return Task.FromResult(false);
        }
        if (state.Messages.TryGetValue(entry.SequenceNumber, out var msg))
        {
            state.Messages[entry.SequenceNumber] = msg with { IsDeferred = true };
        }
        // Don't write to Available - deferred messages are only retrievable by sequence number.
        return Task.FromResult(true);
    }

    public Task<LockedMessage?> TryReceiveDeferredAsync(
        string queueName,
        long sequenceNumber,
        TimeSpan lockDuration,
        string? associatedLinkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!state.Messages.TryGetValue(sequenceNumber, out var stored) || !stored.IsDeferred)
        {
            return Task.FromResult<LockedMessage?>(null);
        }

        // Atomically clear the IsDeferred flag and place under a new lock with WasDeferred=true,
        // so abandon brings it back to Deferred (not Active).
        var unmarked = stored with { IsDeferred = false };
        if (!state.Messages.TryUpdate(sequenceNumber, unmarked, stored))
        {
            // Lost the race - someone else grabbed it concurrently.
            return Task.FromResult<LockedMessage?>(null);
        }

        var lockToken = Guid.NewGuid();
        var lockedUntil = _timeProvider.GetUtcNow() + lockDuration;
        state.Locks[lockToken] = new LockEntry
        {
            SequenceNumber = sequenceNumber,
            LockedUntil = lockedUntil,
            AssociatedLink = associatedLinkName,
            WasDeferred = true,
        };

        return Task.FromResult<LockedMessage?>(new LockedMessage
        {
            Message = unmarked,
            LockToken = lockToken,
            LockedUntil = lockedUntil,
        });
    }

    public IReadOnlyList<StoredMessage> Peek(string queueName, long fromSequenceNumber, int maxCount)
    {
        if (maxCount <= 0 || !_queues.TryGetValue(queueName, out var state))
        {
            return Array.Empty<StoredMessage>();
        }

        // Snapshot, filter by lower bound, sort, take.
        var result = new List<StoredMessage>(Math.Min(maxCount, state.Messages.Count));
        foreach (var (_, msg) in state.Messages)
        {
            if (msg.SequenceNumber >= fromSequenceNumber) result.Add(msg);
        }
        result.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
        if (result.Count > maxCount) result.RemoveRange(maxCount, result.Count - maxCount);
        return result;
    }

    // ── Sessions ──

    public Task<SessionLock?> TryAcceptSessionAsync(
        string queueName,
        string sessionId,
        TimeSpan lockDuration,
        string? linkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        var session = state.GetOrAddSession(sessionId);
        var now = _timeProvider.GetUtcNow();
        lock (session)
        {
            if (session.Lock is not null && session.Lock.LockedUntil > now)
            {
                return Task.FromResult<SessionLock?>(null); // already locked by someone
            }
            session.Lock = new SessionLockEntry
            {
                LockedUntil = now + lockDuration,
                LinkName = linkName,
            };
            return Task.FromResult<SessionLock?>(new SessionLock
            {
                SessionId = sessionId,
                LockedUntil = session.Lock.LockedUntil,
                LinkName = linkName,
            });
        }
    }

    public Task<SessionLock?> TryAcceptNextSessionAsync(
        string queueName,
        TimeSpan lockDuration,
        string? linkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        var now = _timeProvider.GetUtcNow();

        foreach (var (sessionId, session) in state.Sessions)
        {
            // Skip sessions that are already locked or have no available messages.
            if (session.Available.Reader.Count == 0) continue;
            lock (session)
            {
                if (session.Lock is not null && session.Lock.LockedUntil > now) continue;
                session.Lock = new SessionLockEntry
                {
                    LockedUntil = now + lockDuration,
                    LinkName = linkName,
                };
                return Task.FromResult<SessionLock?>(new SessionLock
                {
                    SessionId = sessionId,
                    LockedUntil = session.Lock.LockedUntil,
                    LinkName = linkName,
                });
            }
        }
        return Task.FromResult<SessionLock?>(null);
    }

    public async Task<LockedMessage?> TryDequeueFromSessionAsync(
        string queueName,
        string sessionId,
        TimeSpan messageLockDuration,
        string? linkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!state.Sessions.TryGetValue(sessionId, out var session)) return null;

        // Only the current lock holder may pull from the session. If the lock has expired or
        // been handed to another receiver, return null (without cancellation) so the caller's
        // pump stops instead of interleaving with the new owner - this is the signal the AMQP
        // session receiver treats as "session lock lost".
        if (!IsSessionLockHeldBy(session, linkName))
        {
            return null;
        }

        long seq;
        try
        {
            seq = await session.Available.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }

        if (!state.Messages.TryGetValue(seq, out var stored))
        {
            // Out-of-band removal - recurse.
            return await TryDequeueFromSessionAsync(queueName, sessionId, messageLockDuration, linkName, cancellationToken).ConfigureAwait(false);
        }

        var lockToken = Guid.NewGuid();
        var lockedUntil = _timeProvider.GetUtcNow() + messageLockDuration;
        state.Locks[lockToken] = new LockEntry
        {
            SequenceNumber = seq,
            LockedUntil = lockedUntil,
            AssociatedLink = linkName,
            SessionId = sessionId,
        };

        return new LockedMessage
        {
            Message = stored,
            LockToken = lockToken,
            LockedUntil = lockedUntil,
        };
    }

    public Task<DateTimeOffset?> TryRenewSessionLockAsync(
        string queueName,
        string sessionId,
        TimeSpan lockDuration,
        string? requestingLinkName = null,
        CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (!state.Sessions.TryGetValue(sessionId, out var session)) return Task.FromResult<DateTimeOffset?>(null);

        lock (session)
        {
            // An expired session lock cannot be revived by a late renew - the deadline
            // passing IS the loss, same as message locks.
            if (session.Lock is null || session.Lock.LockedUntil <= _timeProvider.GetUtcNow())
            {
                return Task.FromResult<DateTimeOffset?>(null);
            }
            if (requestingLinkName is not null
                && session.Lock.LinkName is not null
                && !string.Equals(session.Lock.LinkName, requestingLinkName, StringComparison.Ordinal))
            {
                return Task.FromResult<DateTimeOffset?>(null);
            }
            session.Lock.LockedUntil = _timeProvider.GetUtcNow() + lockDuration;
            return Task.FromResult<DateTimeOffset?>(session.Lock.LockedUntil);
        }
    }

    public Task ReleaseSessionAsync(string queueName, string sessionId, string? expectedLinkName = null, CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (state.Sessions.TryGetValue(sessionId, out var session))
        {
            lock (session)
            {
                // Only release the lock we still own. If expectedLinkName is given and the
                // current holder is a different link, another receiver has taken over the
                // session since - leave its lock intact.
                if (expectedLinkName is null
                    || session.Lock is null
                    || session.Lock.LinkName is null
                    || string.Equals(session.Lock.LinkName, expectedLinkName, StringComparison.Ordinal))
                {
                    session.Lock = null;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task SetSessionStateAsync(string queueName, string sessionId, byte[]? state, CancellationToken cancellationToken = default)
    {
        var queue = GetQueue(queueName);
        var session = queue.GetOrAddSession(sessionId);
        session.State = state;
        session.StateUpdatedAt = _timeProvider.GetUtcNow();
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetSessionStateAsync(string queueName, string sessionId, CancellationToken cancellationToken = default)
    {
        var state = GetQueue(queueName);
        if (state.Sessions.TryGetValue(sessionId, out var session))
        {
            return Task.FromResult(session.State);
        }
        return Task.FromResult<byte[]?>(null);
    }

    public IReadOnlyList<string> ListSessions(string queueName, DateTimeOffset? stateUpdatedAfter = null, int skip = 0, int top = int.MaxValue)
    {
        if (!_queues.TryGetValue(queueName, out var state) || top <= 0) return Array.Empty<string>();
        return state.Sessions
            .Where(kv => stateUpdatedAfter is null
                ? kv.Value.Available.Reader.Count > 0 || kv.Value.State is not null
                : kv.Value.State is not null && kv.Value.StateUpdatedAt is { } updatedAt && updatedAt > stateUpdatedAfter.Value)
            .Select(kv => kv.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Skip(Math.Max(skip, 0))
            .Take(top)
            .ToArray();
    }

    public Task<bool> IsSessionLockHeldAsync(string queueName, string sessionId, string? linkName = null, CancellationToken cancellationToken = default)
    {
        if (!_queues.TryGetValue(queueName, out var state)
            || !state.Sessions.TryGetValue(sessionId, out var session))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(IsSessionLockHeldBy(session, linkName));
    }

    private QueueState GetQueue(string queueName)
    {
        if (!_queues.TryGetValue(queueName, out var state))
        {
            throw new InvalidOperationException($"Queue '{queueName}' does not exist.");
        }
        return state;
    }

    /// <summary>
    /// True when <paramref name="session"/> is currently locked, not expired, and (if the lock
    /// records a holder link) held by <paramref name="linkName"/>. Used to keep session receives
    /// exclusive to the lock owner.
    /// </summary>
    private bool IsSessionLockHeldBy(SessionState session, string? linkName)
    {
        var now = _timeProvider.GetUtcNow();
        lock (session)
        {
            if (session.Lock is null || session.Lock.LockedUntil <= now) return false;
            if (session.Lock.LinkName is not null && linkName is not null
                && !string.Equals(session.Lock.LinkName, linkName, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }
    }

    private sealed class TopicDedupState
    {
        public readonly object Sync = new();
        public readonly Dictionary<string, DateTimeOffset> Seen = new(StringComparer.Ordinal);
    }

    private sealed class QueueState
    {
        public long NextSequenceNumber;
        public long EnqueuedTotal;   // cumulative new arrivals (for metrics)
        public long CompletedTotal;  // cumulative completions (for metrics)

        /// <summary>
        /// Guards compound read-modify-write sequences that span more than one of the
        /// concurrent collections below - the dedup check-then-insert, the dequeue
        /// claim-then-lock, and the lock renew-vs-expire pair. Each collection is
        /// individually thread-safe, but those multi-step operations need to be atomic
        /// relative to the TTL/lock sweepers to avoid a message being both delivered and
        /// expired, or a lock surviving its own expiry. Never held across an await.
        /// </summary>
        public readonly object Sync = new();
        public readonly ConcurrentDictionary<long, StoredMessage> Messages = new();
        public readonly ConcurrentDictionary<Guid, LockEntry> Locks = new();
        public readonly Channel<long> Available = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        // Sessions. Lazy per-session state container keyed by SessionId.
        public readonly ConcurrentDictionary<string, SessionState> Sessions = new(StringComparer.Ordinal);

        public SessionState GetOrAddSession(string sessionId) =>
            Sessions.GetOrAdd(sessionId, _ => new SessionState());

        // Duplicate detection sliding window keyed on MessageId. Two parallel maps:
        // SeenMessageIds tracks expiry (for the cheap eviction sweep), OriginalsByMessageId
        // gives us back the StoredMessage so a duplicate send returns the same sequence number.
        public readonly ConcurrentDictionary<string, DateTimeOffset> SeenMessageIds = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, StoredMessage> OriginalsByMessageId = new(StringComparer.Ordinal);
    }

    private sealed class LockEntry
    {
        public required long SequenceNumber { get; init; }
        public required DateTimeOffset LockedUntil { get; set; }
        public string? AssociatedLink { get; init; }
        /// <summary>True when this lock was acquired via receive-by-sequence-number on a deferred
        /// message - abandon returns it to Deferred state, not the Active pool.</summary>
        public bool WasDeferred { get; init; }
        /// <summary>The session id this message came from, if any. Locking continues to hold
        /// the session lock alongside the message lock; release on message disposition.</summary>
        public string? SessionId { get; init; }
    }

    private sealed class SessionState
    {
        public readonly Channel<long> Available = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        /// <summary>Opaque user-supplied state blob. Null means no state has been set (or it was cleared).</summary>
        public byte[]? State;

        /// <summary>When the state blob was last set or updated; null until the first set.</summary>
        public DateTimeOffset? StateUpdatedAt;

        /// <summary>The current session lock, or null if the session is unlocked.</summary>
        public SessionLockEntry? Lock;
    }

    private sealed class SessionLockEntry
    {
        public required DateTimeOffset LockedUntil { get; set; }
        public required string? LinkName { get; init; }
    }
}
