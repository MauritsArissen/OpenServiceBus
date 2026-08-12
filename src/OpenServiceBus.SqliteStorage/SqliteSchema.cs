using Microsoft.Data.Sqlite;

namespace OpenServiceBus.SqliteStorage;

/// <summary>
/// One-shot DDL applied at store startup. Pragmatic single-script migration - when the
/// schema needs to evolve we'll switch to a versioned strategy, but the v1 shape is stable
/// enough that an additive change can just run the same script (every <c>CREATE</c> is
/// <c>IF NOT EXISTS</c>).
///
/// Timestamps are stored as INTEGER (unix milliseconds, UTC) rather than TEXT so range
/// queries like "expires_at &lt; now" use a B-tree scan instead of string comparison.
/// Booleans are INTEGER (0/1) - SQLite has no native bool type.
/// </summary>
internal static class SqliteSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS queues (
            name TEXT PRIMARY KEY COLLATE NOCASE
        );

        -- Opaque descriptor snapshots (JSON) so entity settings - status, lock duration,
        -- session flag, ... - survive a restart, not only the queue names.
        CREATE TABLE IF NOT EXISTS queue_descriptors (
            queue_name      TEXT PRIMARY KEY COLLATE NOCASE,
            descriptor_json TEXT NOT NULL,
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS sequence_counters (
            queue_name TEXT PRIMARY KEY COLLATE NOCASE,
            next_sequence INTEGER NOT NULL,
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS messages (
            queue_name              TEXT NOT NULL COLLATE NOCASE,
            sequence_number         INTEGER NOT NULL,
            enqueued_at             INTEGER NOT NULL,
            encoded_message         BLOB NOT NULL,
            delivery_count          INTEGER NOT NULL DEFAULT 0,
            expires_at              INTEGER NULL,
            scheduled_enqueue_time  INTEGER NULL,
            is_deferred             INTEGER NOT NULL DEFAULT 0,
            session_id              TEXT NULL,
            PRIMARY KEY (queue_name, sequence_number),
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        -- Composite index covers the dequeue predicate: by queue, only non-deferred,
        -- only currently-available (scheduled_enqueue_time IS NULL OR <= now), ordered by sequence.
        CREATE INDEX IF NOT EXISTS idx_messages_available
            ON messages (queue_name, is_deferred, scheduled_enqueue_time, sequence_number);

        CREATE INDEX IF NOT EXISTS idx_messages_session
            ON messages (queue_name, session_id, is_deferred, sequence_number);

        CREATE INDEX IF NOT EXISTS idx_messages_expires
            ON messages (queue_name, expires_at)
            WHERE expires_at IS NOT NULL;

        CREATE TABLE IF NOT EXISTS locks (
            lock_token       TEXT PRIMARY KEY,
            queue_name       TEXT NOT NULL COLLATE NOCASE,
            sequence_number  INTEGER NOT NULL,
            locked_until     INTEGER NOT NULL,
            associated_link  TEXT NULL,
            was_deferred     INTEGER NOT NULL DEFAULT 0,
            session_id       TEXT NULL,
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_locks_queue_seq
            ON locks (queue_name, sequence_number);

        CREATE INDEX IF NOT EXISTS idx_locks_expiry
            ON locks (queue_name, locked_until);

        CREATE TABLE IF NOT EXISTS session_locks (
            queue_name    TEXT NOT NULL COLLATE NOCASE,
            session_id    TEXT NOT NULL,
            locked_until  INTEGER NOT NULL,
            link_name     TEXT NULL,
            PRIMARY KEY (queue_name, session_id),
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS session_state (
            queue_name  TEXT NOT NULL COLLATE NOCASE,
            session_id  TEXT NOT NULL,
            state       BLOB NULL,
            PRIMARY KEY (queue_name, session_id),
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        -- Dedup history. We retain the original sequence number so a second send with
        -- the same MessageId can return the *original* StoredMessage (matching Azure SB's
        -- semantics where the duplicate disposition is silently accepted).
        CREATE TABLE IF NOT EXISTS dedup_history (
            queue_name                 TEXT NOT NULL COLLATE NOCASE,
            message_id                 TEXT NOT NULL,
            original_sequence_number   INTEGER NOT NULL,
            expires_at                 INTEGER NOT NULL,
            PRIMARY KEY (queue_name, message_id),
            FOREIGN KEY (queue_name) REFERENCES queues(name) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_dedup_expiry
            ON dedup_history (queue_name, expires_at);

        -- Topic descriptor snapshots (JSON). Topics have no row in `queues` (they store no
        -- messages), so this table stands alone and rows are removed explicitly on delete.
        CREATE TABLE IF NOT EXISTS topic_descriptors (
            topic_name      TEXT PRIMARY KEY COLLATE NOCASE,
            descriptor_json TEXT NOT NULL
        );

        -- Topic-level dedup history: one check per publish, before fan-out. No original
        -- sequence number - topics store no messages, only the drop decision matters.
        CREATE TABLE IF NOT EXISTS topic_dedup_history (
            topic_name  TEXT NOT NULL COLLATE NOCASE,
            message_id  TEXT NOT NULL,
            expires_at  INTEGER NOT NULL,
            PRIMARY KEY (topic_name, message_id)
        );

        CREATE INDEX IF NOT EXISTS idx_topic_dedup_expiry
            ON topic_dedup_history (topic_name, expires_at);
        """;

    /// <summary>
    /// Applied once per connection on Open. PRAGMAs are connection-scoped (except a few),
    /// so they're set every time we open a fresh connection from the pool.
    /// </summary>
    public const string ConnectionPragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        """;

    public static void Apply(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ConnectionPragmas + "\n" + Sql;
        cmd.ExecuteNonQuery();
    }
}
