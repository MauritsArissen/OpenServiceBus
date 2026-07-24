# Persistence

OpenServiceBus ships **two** `IMessageStore` implementations. They share the interface
exactly, so picking one is a single DI call - every feature (peek-lock, sessions, dedup,
transactions, auto-forwarding, …) works against either.

|                  | `InMemoryMessageStore`                    | `SqliteMessageStore`                                                   |
| ---------------- | ----------------------------------------- | ---------------------------------------------------------------------- |
| Persistence      | Process-lifetime only                     | Single `.db` file (or `:memory:`)                                      |
| Concurrency      | Lock-free channels + ConcurrentDictionary | Single connection, async-serialized via SemaphoreSlim                  |
| Best for         | Tests, ephemeral dev                      | Docker, long-running brokers, fixtures that need restart               |
| Dequeue latency  | Sub-millisecond                           | Sub-millisecond on hot path; polling fallback at `DequeuePollInterval` |
| Schema migration | N/A                                       | Idempotent DDL on every open (`IF NOT EXISTS`)                         |

## Enable SQLite

### Via the standalone host

```json
{
  "OpenServiceBus": {
    "Storage": {
      "Mode": "Sqlite",
      "DataSource": "/var/lib/openservicebus/broker.db"
    }
  }
}
```

Or env vars:

```bash
export OPENSERVICEBUS__STORAGE__MODE=Sqlite
export OPENSERVICEBUS__STORAGE__DATASOURCE=/data/broker.db
dotnet run --project src/OpenServiceBus.Host
```

### Via custom hosting

```csharp
services.AddOpenServiceBusSqliteStorage(opt =>
{
    opt.DataSource = "/data/broker.db";          // or ":memory:"
    opt.DequeuePollInterval = TimeSpan.FromMilliseconds(250);
});
services.AddOpenServiceBusInMemoryStorage();      // still registers registries + router + tx manager
services.AddOpenServiceBusAmqp();
```

`AddOpenServiceBusSqliteStorage` registers `IMessageStore` first; the in-memory DI's
`TryAddSingleton` then becomes a no-op for the store. Registries (queue/topic) stay in
memory either way and are rehydrated from the store on startup.

## Schema

```sql
CREATE TABLE queues (name TEXT PRIMARY KEY COLLATE NOCASE);

CREATE TABLE sequence_counters (
    queue_name TEXT PRIMARY KEY COLLATE NOCASE,
    next_sequence INTEGER NOT NULL
);

CREATE TABLE messages (
    queue_name              TEXT,
    sequence_number         INTEGER,
    enqueued_at             INTEGER,        -- unix ms
    encoded_message         BLOB,           -- raw AMQP bytes
    delivery_count          INTEGER DEFAULT 0,
    expires_at              INTEGER,        -- unix ms or NULL
    scheduled_enqueue_time  INTEGER,        -- unix ms or NULL
    is_deferred             INTEGER DEFAULT 0,
    session_id              TEXT,
    PRIMARY KEY (queue_name, sequence_number)
);

CREATE TABLE locks (
    lock_token       TEXT PRIMARY KEY,
    queue_name       TEXT,
    sequence_number  INTEGER,
    locked_until     INTEGER,
    associated_link  TEXT,
    was_deferred     INTEGER DEFAULT 0,
    session_id       TEXT
);

CREATE TABLE session_locks    ( queue_name, session_id, locked_until, link_name );
CREATE TABLE session_state    ( queue_name, session_id, state BLOB );
CREATE TABLE dedup_history    ( queue_name, message_id, original_sequence_number, expires_at );
```

PRAGMAs set on every open:

- `journal_mode = WAL`
- `synchronous = NORMAL`
- `foreign_keys = ON`
- `busy_timeout = 5000`

WAL means readers don't block writers - and the schema is small/normalized enough that you
can `sqlite3 broker.db` and inspect everything by hand.

## Restart semantics

When the broker restarts against an existing `.db` file:

1. SQLite opens, applies idempotent DDL (`IF NOT EXISTS`).
2. `QueueRehydrationHostedService` runs after the config bootstrap. It calls
   `IMessageStore.ListQueueNames()` and, for any queue not already in the registry,
   creates a `QueueDescriptor` with **default settings** (lock 60s, max-delivery 10, no
   sessions, no dedup, etc.).
3. Messages are immediately receivable - sequence numbers and delivery counts survive.

> 💡 **Per-queue settings are NOT persisted in the SQLite schema today.** They live only in
> `QueueDescriptor` objects in memory. If you need declarative settings to survive
> restarts, mount a `config.json` - the bootstrap service runs before rehydration, so
> config-declared queues come up with the right shape and rehydration is a no-op for them.

> ⚠️ **Topics, subscriptions, and rules are not rehydrated at all.** Only queue names come
> back from the store. Subscription backing queues (`events/Subscriptions/all`) resurface
> as plain queues with their messages intact, but the topic itself, its subscription list,
> and its filter rules are gone - senders attaching to the topic fail and fan-out stops.
> If you use topics with SQLite persistence, declaring them in `config.json` is required,
> not optional.

## File location tips

- **Don't** put the `.db` on tmpfs unless you want ephemeral mode (you already have `:memory:` for that).
- **Do** put it on a real persistent volume in containers (`docker volume create osb-data && docker run -v osb-data:/data ...`).
- **Don't** point two brokers at the same `.db` simultaneously - SQLite serializes writes but the in-memory registries would diverge. Run one broker process per file.
- **Backups** are a single `sqlite3 broker.db ".backup broker.bak"` away (the WAL file is checkpointed automatically on graceful shutdown).

## Tests

The SQLite store passes the same SDK-level test suite as the in-memory one - see
[`tests/OpenServiceBus.SqliteStorage.Tests`](https://github.com/mauritsarissen/OpenServiceBus/tree/main/tests/OpenServiceBus.SqliteStorage.Tests).
Highlights:

- **19 store-level tests** covering every `IMessageStore` method (queues, peek-lock, defer,
  schedule, dedup, sessions, TTL, peek), including a **disk-durability test** that opens a
  fresh `SqliteMessageStore` against the same `.db` and reads the message a previous
  instance wrote.
- **5 SDK round-trip tests** booting the full broker with SQLite as the backing store,
  driving via the real `Azure.Messaging.ServiceBus` client.
