# Background Sweepers

Three background services keep time-based message state moving without any client attached:

| Service | Base interval | What it does |
| --- | --- | --- |
| `LockManager` | 100 ms | Releases expired peek-locks so their messages are redelivered |
| `TtlExpirationService` | 500 ms | Removes (or dead-letters) messages past their TTL deadline |
| `ScheduledMessageActivator` | 500 ms | Makes scheduled messages available when their time arrives, and fans out due topic publishes |

(A fourth, the `IdleEntityReaper` for `AutoDeleteOnIdle`, works off the in-memory entity
registry and activity tracker rather than the message store, so none of this page applies
to it.)

## One store call per tick

Each sweeper tick is a single store-wide operation - `ExpireLocksAll`,
`ExpireMessagesAll`, `ActivateScheduledAll` on `IMessageStore` - instead of one store call
per queue. That distinction is invisible on a small topology and decisive on a large one:
every subscription has its own backing queue, so a suite with ~100 topics and a few
subscriptions each used to produce thousands of store calls per second from the sweepers
alone. On the SQLite store each of those calls takes the store's global lock and runs a
SQL command, so with enough entities the sweep traffic saturated the lock and client
operations (sends, admin calls, `/purge`) timed out - a broker that looked hung while
doing nothing (issue #90).

With the store-wide operations the sweep cost no longer grows with the number of
entities. The SQLite implementations resolve an idle tick with a probe on dedicated
partial indexes (`idx_messages_expires_global`, `idx_messages_scheduled_global`,
`idx_locks_expiry_global` - added automatically to existing database files at startup);
the in-memory implementation skips queues with no messages or locks without leaving the
store.

The per-queue APIs (`ExpireLocks`, `ExpireMessages`, `ActivateScheduled`) remain for
tests that drive expiration deterministically with a `FakeTimeProvider`, and external
`IMessageStore` implementations keep working: the store-wide members have default
implementations that loop the per-queue calls.

## Idle backoff

Every store-wide sweep also reports whether anything relevant is left in the store -
unexpired locks, messages carrying a TTL, future-dated scheduled messages. While there is
nothing, each sweeper doubles its timer up to a cap and resets to its base interval the
moment a sweep finds work or pending state:

| Service | Base | Idle cap |
| --- | --- | --- |
| `LockManager` | 100 ms | 1 s |
| `TtlExpirationService` | 500 ms | 2 s |
| `ScheduledMessageActivator` | 500 ms | 1 s |

The caps bound the worst-case extra latency for the first event after an idle period (a
lock released up to ~1 s late, a TTL swept up to ~2 s late); once anything is pending the
sweepers run at their base cadence, so steady-state timing is unchanged. The dequeue path
checks TTL and lock deadlines itself, so client-visible correctness never depends on a
sweep arriving on time - the sweepers are housekeeping for idle entities.

External stores that cannot answer the pending question cheaply should report
`HasPending: true` (the default implementations do), which simply disables the backoff.
