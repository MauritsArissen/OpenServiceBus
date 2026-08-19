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
| Schema migration | N/A                                       | Idempotent DDL on every open (`IF NOT EXISTS` + additive `ALTER`s)     |

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
CREATE TABLE session_state    ( queue_name, session_id, state BLOB, updated_at INTEGER );
CREATE TABLE dedup_history    ( queue_name, message_id, original_sequence_number, expires_at );

-- Entity settings, as opaque JSON snapshots written on every create/update.
CREATE TABLE queue_descriptors (
    queue_name      TEXT PRIMARY KEY COLLATE NOCASE,
    descriptor_json TEXT NOT NULL
);

CREATE TABLE topic_descriptors (
    topic_name      TEXT PRIMARY KEY COLLATE NOCASE,
    descriptor_json TEXT NOT NULL
);

CREATE TABLE subscription_descriptors (
    topic_name        TEXT COLLATE NOCASE,
    subscription_name TEXT COLLATE NOCASE,
    descriptor_json   TEXT NOT NULL,
    PRIMARY KEY (topic_name, subscription_name)
);

-- One row per rule: filter (True/False/SQL/correlation, with parameters) and optional action.
CREATE TABLE subscription_rules (
    topic_name        TEXT COLLATE NOCASE,
    subscription_name TEXT COLLATE NOCASE,
    rule_name         TEXT COLLATE NOCASE,
    rule_json         TEXT NOT NULL,
    PRIMARY KEY (topic_name, subscription_name, rule_name)
);

CREATE TABLE topic_dedup_history ( topic_name, message_id, expires_at );
```

Deletes cascade top-down: dropping a topic removes its subscription and rule rows, and
dropping a subscription removes its rules - no orphans are left behind for a later
rehydration to resurrect.

PRAGMAs set on every open:

- `journal_mode = WAL`
- `synchronous = NORMAL`
- `foreign_keys = ON`
- `busy_timeout = 5000`

WAL means readers don't block writers - and the schema is small/normalized enough that you
can `sqlite3 broker.db` and inspect everything by hand.

## Restart semantics

When the broker restarts against an existing `.db` file:

1. SQLite opens, applies idempotent DDL (`IF NOT EXISTS`) and adds any columns
   newer broker versions introduced (currently `session_state.updated_at`) via a
   guarded `ALTER TABLE`, so databases from older versions keep working.
2. `QueueRehydrationHostedService` runs after the config bootstrap and hands off to
   `EntityRehydrator`, which restores, in order:
   1. **Topics** from `topic_descriptors` - full settings (status, TTL, duplicate
      detection), including topics that have no subscriptions yet.
   2. **Subscriptions** from `subscription_descriptors` - full settings, `RequiresSession`,
      `ForwardTo`, `AutoDeleteOnIdle` and `UserMetadata` included.
   3. **Queues** from `queue_descriptors` - full settings (lock duration, max-delivery,
      sessions, dedup, status, forwarding).
   4. **Rules** from `subscription_rules` - every filter shape (True, False, SQL with
      parameters, correlation with typed properties) plus the optional SQL action.
3. Messages are immediately receivable - sequence numbers and delivery counts survive.

The restored rule set **replaces** the `$Default` TrueFilter a fresh subscription is born
with, rather than merging alongside it. A `$Default` you deleted stays deleted; without
that, every rehydrated subscription would silently widen back to match-all.

> 💡 **`config.json` still wins where it conflicts.** The bootstrap service runs before
> rehydration, so anything it declares is left exactly as declared and its subscriptions
> keep their config-declared rules. Declaring topology in config is now an option rather
> than a requirement.

> ⚠️ **Databases written by older versions** have no `subscription_descriptors` rows. Those
> subscriptions still come back through the legacy backing-queue-name scan, which recovers
> only what the backing queue mirrors (lock duration, max-delivery, TTL, dead-lettering on
> expiration, dead-letter forwarding, status) and gives them a `$Default` TrueFilter. Once
> the subscription is next created or updated, it gets a real snapshot.

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
- **Topology rehydration tests** that build a topic, subscriptions and custom rules against
  a `.db` file, close the store, reopen it and run `EntityRehydrator` over fresh registries
  - asserting the restored filters still route (and still reject) the same messages.
