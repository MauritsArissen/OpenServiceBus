# Architecture

The broker is split across small, single-purpose assemblies so consumers can pick exactly
what they need (e.g. just `OpenServiceBus.Testing` for tests, no Web/Kestrel deps).

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              OpenServiceBus.Host                             │
│  ASP.NET Core (Kestrel) - management REST + /health · binds storage mode    │
└─────┬──────────────────────────────────────────────────────────────────┬─────┘
      │                                                                  │
┌─────▼─────────────────────────────────────────────────┐  ┌─────────────▼────────┐
│             OpenServiceBus.Management                 │  │ OpenServiceBus.Amqp  │
│  REST endpoints for queues/topics/subscriptions/rules │  │ AMQP 1.0 listener,   │
└─────┬─────────────────────────────────────────────────┘  │ $cbs, $management,   │
      │                                                    │ link routing,        │
┌─────▼──────────────────────────────────────┐             │ WebSocket bridge,    │
│       OpenServiceBus.InMemoryStorage       │             │ OTel hooks           │
│  InMemoryMessageStore, QueueManager,       │◄────────────┤                      │
│  TopicManager, LockManager, MessageRouter, │             └──────┬───────────────┘
│  TransactionManager                        │                    │
└─────┬──────────────────────────────────────┘  ┌─────────────────▼──────────────┐
      │                                          │     OpenServiceBus.SqliteStorage│
      │  IMessageStore / IQueueRegistry /        │     SQLite IMessageStore        │
      │  ITopicRegistry / IMessageRouter /       │     (alternative to in-memory)  │
      │  ITransactionManager                     │                                 │
      └────────────────┬─────────────────────────┴────────────────┬────────────────┘
                       │                                          │
                ┌──────▼──────────────────────────────────────────▼──────┐
                │              OpenServiceBus.Core                       │
                │  Domain types: StoredMessage, QueueDescriptor,         │
                │  TopicDescriptor, SubscriptionDescriptor, RuleDescriptor│
                │  Filters, EmulatorConfig, OpenServiceBusDiagnostics    │
                └─────────────────────────────────────────────────────────┘
                       ▲
                       │
        ┌──────────────┴────────────────┐
        │     OpenServiceBus.Testing    │
        │  OpenServiceBusTestHost       │
        │  (one-call broker for tests)  │
        └───────────────────────────────┘
```

## Package contracts

| Package                               | Purpose                                                                                                                                                                   | Depends on                                                                |
| ------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| **`OpenServiceBus.Core`**             | Domain types, interfaces, filter language, `config.json` POCOs, OpenTelemetry primitives. **Zero NuGet deps.**                                                            | -                                                                         |
| **`OpenServiceBus.InMemoryStorage`**  | Default in-memory `IMessageStore`, registries, lock manager, message router, transaction manager.                                                                         | Core, MS.Extensions.{Hosting.Abstractions, Logging.Abstractions, Options} |
| **`OpenServiceBus.SqliteStorage`**    | SQLite-backed `IMessageStore`. Drop-in replacement for the in-memory store.                                                                                               | Core, Microsoft.Data.Sqlite, MS.Extensions.\*                             |
| **`OpenServiceBus.Amqp`**             | AMQP 1.0 listener via [AMQPNetLite](https://github.com/Azure/amqpnetlite), `$cbs`, `$management`, sender/receiver processors, link router, WebSocket bridge, diagnostics. | AMQPNetLite, Core                                                         |
| **`OpenServiceBus.Management`**       | ASP.NET Core minimal-API endpoints for queues/topics/subscriptions/rules.                                                                                                 | Core, ASP.NET Core                                                        |
| **`OpenServiceBus.Testing`**          | `OpenServiceBusTestHost` - embeddable broker for test fixtures. Pulls everything together.                                                                                | Amqp, InMemoryStorage                                                     |
| **`OpenServiceBus.Host`** _(app)_     | Standalone executable wiring all of the above behind Kestrel. Reads config / env vars.                                                                                    | All libraries                                                             |
| **`OpenServiceBus.Explorer`** _(app)_ | Browser-based UI for sending/receiving messages and exploring entities.                                                                                                   | None at runtime - talks to the Host's REST API                            |

## Key abstractions in Core

- **`IMessageStore`** - message-level persistence. Implementations: `InMemoryMessageStore`, `SqliteMessageStore`. ~25 methods covering CRUD, peek-lock dispositions, scheduled, defer, sessions, dedup.
- **`IQueueRegistry`** - queue _descriptors_ (settings, not messages). In-memory only today; rehydrates from the store on startup when SQLite is in use.
- **`ITopicRegistry`** - topics + subscriptions + rules. In-memory; subscription backing queues live in `IMessageStore`.
- **`IMessageRouter`** - resolves "where should this message actually land?" Walks `ForwardTo` chains, fans out at topics, caps depth at 4. See [Auto-Forwarding](Auto-Forwarding).
- **`ITransactionManager`** - buffers ops under an AMQP txn-id; commit replays in order, rollback discards. See [Transactions](Transactions).

## AMQP layer

The AMQP listener is a thin shell around `Amqp.Listener.ContainerHost` from AMQPNetLite.
`EntityLinkProcessor` is the single `ILinkProcessor` registered with the host - it inspects
every attach and routes to the right processor:

```
attach (sender)   → /<queue>                  → QueueSenderProcessor
attach (sender)   → /<topic>                  → TopicSenderProcessor (fan-out)
attach (sender)   → Target=Coordinator         → CoordinatorProcessor (transactions)
attach (receiver) → /<queue>                  → QueueReceiverSource (plain peek-lock)
attach (receiver) → /<queue> + SessionFilter   → SessionReceiverSource
attach (receiver) → /<queue>/$DeadLetterQueue  → QueueReceiverSource (DLQ mode)
attach (receiver) → /<topic>/Subscriptions/<s> → QueueReceiverSource on backing queue
$management       → ManagementRequestProcessor (renew, peek, schedule, sessions, rules, …)
$cbs              → CbsRequestProcessor (token validation when SAS enabled)
```

Three wire details worth knowing about dispositions, matching real Service Bus:

- Settling a peek-lock disposition ECHOES the receiver's outcome (Rejected for
  dead-letter, Modified for abandon/defer, Accepted for complete) instead of blanket
  Accepted. The Java SDK (proton-j) verifies the echoed outcome type and fails every
  `deadLetter()`/`abandon()`/`defer()` call otherwise; the other SDKs do not check.
- The `DeadLetterReason` / `DeadLetterErrorDescription` entries on a dead-letter
  disposition's error info arrive with Symbol keys from the .NET SDK but plain string
  keys from proton-j. Both shapes are accepted, like the real service does.
- The `PropertiesToModify` map the SDK settlement overloads carry (abandon / defer /
  dead-letter) is merged into the stored message's application properties before the
  settle, last write wins per key - so redeliveries, deferred retrievals and DLQ copies
  carry it, and it survives a restart on the SQLite store. It arrives as the `Modified`
  outcome's message-annotations field, as extra entries in the dead-letter error info,
  or as `properties-to-modify` on the `$management` update-disposition used when
  settling deferred messages. On a dead-letter, the broker-stamped reason/description
  win over a colliding property key. The Python SDK does not expose the parameter.

## Storage swap

The `IMessageStore` interface is the seam between the AMQP/management surface and the
actual data. To switch from in-memory to SQLite, just register a different singleton:

```csharp
// In-memory (default)
services.AddOpenServiceBusInMemoryStorage();

// SQLite-backed
services.AddOpenServiceBusSqliteStorage(opt => opt.DataSource = "/data/broker.db");
services.AddOpenServiceBusInMemoryStorage();  // still needed for the registry + router
```

The in-memory DI registers `IMessageStore` with `TryAddSingleton`, so registering
SQLite first makes it the active store. Registries (queue/topic) stay in-memory in both
modes - `QueueRehydrationHostedService` reads `IMessageStore.ListQueueNames()` at startup
so persistent stores re-surface their queues to the registry.
