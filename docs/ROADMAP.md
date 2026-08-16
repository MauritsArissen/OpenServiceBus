# OpenServiceBus - post-v1.0 roadmap

v1.0 proved the protocol surface (queues + Functions trigger end-to-end via real AMQP 1.0).
This document captures the work planned after that, grouped into shippable phases. Each
phase ends with a tagged release; you don't have to ship the whole arc to make it useful.

## Status (as of v1.2.x, July 2026)

Everything in **Phase 2 is shipped**: topics + subscriptions + filters, sessions,
duplicate detection, auto-forwarding, and transactions. Most of **Phase 3 is shipped**:
SQLite persistence, OpenTelemetry, and AMQP-over-WebSocket; backpressure/memory bounds
remain open. From **Phase 4**, the multi-arch Docker image (broker + Explorer) and most
of the Explorer v2 feature list are shipped; Bicep/ARM bootstrap, the CLI, the Helm
chart, and the standalone docs site are still open. Individual milestones below are
marked accordingly - the original scope text is kept for reference.

Known gaps within shipped milestones (tracked as bugs/limitations, see the feature docs):
transactional batched sends to topics, commit acknowledgement racing the replay, and SAS
enforcement for non-SDK clients that skip `$cbs`. SQLite rehydration now covers the full
topology - queues, topics, subscriptions and subscription rules - so `config.json` is a
declarative convenience rather than a requirement for topics.

## Locked decisions

- **Bicep/ARM bootstrap lands late (Phase 4)** so it can map the full Service Bus
  resource surface from day one - including topics, subscription rules, forwarding,
  sessions, and duplicate detection.
- **Transactions are in-scope for Phase 2.** v1.x targets genuine Service Bus parity,
  not a subset.
- **Persistence is SQLite-only**. The "zero-dependency, embeddable in your test
  fixture" pitch is the differentiator. SQL Server parity with the official emulator is
  not in scope unless real demand surfaces.
- **Cluster/HA, schema registry, claim-check helpers, partitioning** are explicitly out of
  scope. This stays a single-node test/dev broker.

---

## Phase 2 - Service Bus feature parity (v1.1 → v1.6)

The biggest gaps in the SB feature surface. Ordered so each milestone builds on the
routing/storage primitives the previous one added.

### Topics + Subscriptions + Filters (v1.1) - SHIPPED

The flagship feature of Phase 2 and the largest single milestone in the roadmap.

- New entity types: `TopicDescriptor`, `SubscriptionDescriptor`, `RuleDescriptor`.
- New AMQP routing: `topic-name`, `topic-name/subscriptions/sub-name`,
  `topic-name/subscriptions/sub-name/$DeadLetterQueue`.
- Filter language support:
  - `TrueFilter` / `FalseFilter` (trivial)
  - `CorrelationFilter` (property equality match - easy)
  - `SqlFilter` (subset of T-SQL: comparison, AND/OR/NOT, property references,
    `LIKE`, `IS NULL`, `EXISTS`). Builds on the existing SqlFilter parser shape from
    the Azure SDK.
- New `$management` ops: `create-rule`, `delete-rule`, `get-rules`,
  `set-subscription-rule-action`.
- Send path: enqueue to topic → fan-out to subscriptions whose filter matches → each
  subscription gets its own copy with its own peek-lock lifecycle.
- Tests: in-memory store unit tests, AMQP wire tests, Azure SDK integration tests
  for fan-out, filter matching, and rule CRUD via the SDK's `AdministrationClient`
  (the one feature we'll need to revisit from an earlier decision).

**Gate:** SDK publishes to a topic with three subscriptions on different SQL filters;
only matching subs receive the message.

### Sessions (v1.2) - SHIPPED (queues and topic subscriptions)

Can run parallel to topics work - independent code paths.

- New queue/subscription property: `RequiresSession`.
- `SessionId` propagation on send and receive.
- Session-locked receive: a receiver acquires an exclusive lock on the first
  `SessionId` it sees on the entity and gets only messages with that id until lock
  release.
- Session state via `$management` ops: `set-session-state`, `get-session-state`,
  `renew-session-lock`.
- `get-message-sessions` enumeration op.
- Lock manager extension: lock object = (entity, sessionId) instead of (entity, message).

**Gate:** SDK `CreateSessionReceiverAsync` + ordered receive of all messages with the
same `SessionId` + state round-trip.

### Duplicate detection (v1.3) - SHIPPED

Smallest milestone - a quick win after the heavier .

- New queue property: `RequiresDuplicateDetection`, `DuplicateDetectionHistoryTimeWindow`.
- Per-entity sliding-window hash set keyed on `MessageId`.
- Send path: if `MessageId` is in the window, silently drop (Service Bus's actual
  behavior - not an error, the SDK is unaware).
- Time-window eviction driven by `TimeProvider`.

**Gate:** Send the same `MessageId` twice within the window → receiver sees only one.

### Auto-forwarding (v1.4) - SHIPPED

Builds on the routing layer.

- New queue/subscription properties: `ForwardTo`, `ForwardDeadLetteredMessagesTo`.
- On enqueue (or DLQ-arrival), the broker re-sends to the configured target.
- Loop detection via a forward-hop counter (`x-opt-forward-count` annotation,
  matching Azure's behavior); reject with `amqp:not-allowed` past 4 hops.
- Cross-entity-type forwarding (queue → topic, topic-sub → queue) works because of
  the unified routing.

**Gate:** Send to queue A configured `ForwardTo: B` → message lands in B only.
Loop A→B→A → 4th hop rejected.

### Transactions (v1.5) - SHIPPED (known gaps listed in docs/Transactions.md)

The complex one. The AMQP spec is precise but the implementation has many edge cases.

- AMQP coordinator role on a dedicated link.
- `declare` / `discharge` operations.
- Atomic group of sends, completes, dead-letters, defers across one or many entities.
- Two-phase commit semantics: discharge with `fail = true` rolls everything back.
- `tx-id` propagation on Transfer and Disposition frames.
- Cross-entity transactions (queue + topic + DLQ in the same transaction).

**Gate:** SDK `ServiceBusClient.CreateTransactionalBatch()` round-trip with mixed
ops across two queues, committed atomically; same flow rolled back on discharge-fail.

### v1.5 hardening + release (v1.5 final)

Bug bash, wire-protocol conformance test expansion, performance baseline run. Not a
feature milestone - a release-stabilization milestone.

---

## Phase 3 - Production readiness (v1.6 → v1.9)

The "this broker can run unattended for weeks" phase.

### Persistent storage (SQLite) (v1.6) - SHIPPED (idempotent DDL instead of versioned migrations; descriptors/topology still rehydrate from config.json)

- `OpenServiceBus.SqliteStorage` package exposing `ISqlitePersistenceOptions` and an
  `AddOpenServiceBusSqliteStorage` DI extension.
- Same `IMessageStore` contract as in-memory - pluggable, no broker-code changes.
- Single SQLite file (configurable path); WAL mode for durability + concurrency.
- Schema versioning + EF-Core-style migrations on startup.
- Crash recovery: on restart, redeliver any messages that were under peek-lock when
  the process died (lock-expired-on-restart).
- Performance gate: 10k msg/sec sustained send+receive on a laptop SSD.

### OpenTelemetry (v1.7) - SHIPPED

- `ActivitySource` for AMQP frame lifecycle, message lifecycle, lock lifecycle.
- Metric instruments: queue depth (gauge), message-rate by op (counter), lock
  duration histogram, DLQ-rate counter.
- Structured logging through `Microsoft.Extensions.Logging` with stable category
  names per subsystem.
- OTel resource attributes (`service.namespace`, `service.name`, `service.version`).

### AMQP-over-WebSocket (v1.8) - SHIPPED (as a bridge on its own port rather than on the Kestrel host)

- ASP.NET Core WebSocket endpoint on the same Kestrel host as the management API.
- Routes `Upgrade: websocket` requests through AMQPNetLite's `WebSocketTransport`.
- Compatible with `ServiceBusTransportType.AmqpWebSockets` (the SDK's setting for
  firewalled environments).
- Tested end-to-end with the Azure SDK in WebSocket mode.

### Backpressure + memory bounds (v1.9) - OPEN

- Per-queue depth ceiling (configurable, default 100k messages or 256 MB whichever
  first).
- On overflow: reject sends with `amqp:resource-limit-exceeded`.
- Lock-table size ceiling - reject `TryDequeue` past a limit.
- Memory pressure observer hooked into GC notifications.
- Stress test in CI: 1M-message burst doesn't OOM, gracefully rejects past the cap.

---

## Phase 4 - Tooling & ecosystem (v2.0)

This phase makes OpenServiceBus pleasant to use, not just functional.

### Bicep / ARM template bootstrap (v2.0) - OPEN

The single most-requested ergonomic feature.

- `OpenServiceBus.BicepBootstrap` package.
- Two input shapes:
  - **`.bicep` file** - compiled in-process via the Bicep CLI (`bicep build --stdout`)
    or via `Microsoft.Bicep.Core` if its public API has matured.
  - **`.json` ARM template** - parsed directly; faster and dependency-free.
- Recognizes the full `Microsoft.ServiceBus/*` resource family:
  - `Microsoft.ServiceBus/namespaces` (namespace-level config - informational only)
  - `Microsoft.ServiceBus/namespaces/queues`
  - `Microsoft.ServiceBus/namespaces/topics`
  - `Microsoft.ServiceBus/namespaces/topics/subscriptions`
  - `Microsoft.ServiceBus/namespaces/topics/subscriptions/rules`
- Maps every property landed in Phase 2 () to OpenServiceBus's descriptors.
- Logs `parameters` and `outputs` blocks as info; doesn't try to evaluate ARM
  expressions beyond what's needed for resource names.
- Warns on features still not supported (premium-tier-only fields, geo-replication,
  private endpoints, etc.) and continues.
- Shipped alongside the existing `config.json` loader; both formats coexist.
- Resolution order: `--bicep <path>` → `--config <path>` → env vars → defaults.

**Gate:** Take a real production Bicep file used by an Azure deployment, point
OpenServiceBus at it, get a broker shaped identically (modulo deferred features).

### Explorer v2 (v2.0) - MOSTLY SHIPPED (topics tree, rule editor, DLQ requeue, metrics; filter-test preview, scheduled timeline, and session viewer still open)

- Topics + subscription tree view.
- Subscription rule editor with a **"test this message against this filter"**
  preview that runs the actual filter evaluator and shows match/no-match.
- Message-property inspector (all `x-opt-*` annotations surfaced).
- Scheduled-message timeline view.
- DLQ tools: requeue-from-DLQ, reason filtering.
- Session viewer.
- All built on the management REST API - no Explorer-specific server code.

### `openservicebus` CLI (v2.0) - OPEN

- Scriptable wrapper around the management REST API.
- Verbs: `queue create/delete/list/describe`, `topic create`, `subscription create`,
  `rule create/list`, `message send/peek/drain`, `dlq move`, `health`.
- Output formats: `--output json|table|jsonl` for piping into other tools.
- Connection string read from `OPENSERVICEBUS_CONNECTION` env var or `--endpoint`.

### Docker image + Helm chart (v2.0) - IMAGE SHIPPED, HELM CHART OPEN

- Multi-arch (amd64, arm64) `mauritsarissen/openservicebus` image.
- Bundled `config.json`/Bicep bootstrap support via mounted volume.
- SQLite persistence path mounted from a volume.
- Health/readiness probes wired through the existing `/health` endpoint.
- Helm chart with sensible defaults; PVC for the SQLite file; ServiceMonitor for
  the OTel metrics.

### Documentation site (v2.0) - OPEN (docs/ currently syncs to the GitHub wiki instead)

- mkdocs-material site at `docs.openservicebus.dev` (or similar).
- Sections: Getting Started, Concepts (queues/topics/subs/sessions/transactions),
  How-to (test fixtures, Bicep, Docker), Reference (config.json, Bicep mapping,
  REST API, CLI), Architecture (current docs/PLAN.md fits here).
- Built and deployed via GitHub Actions on every push to `main`.
- Replaces the loose `docs/*.md` files in the repo (they migrate into the site
  source).

---

## Release cadence

| Phase   | Releases    | Target                                            |
| ------- | ----------- | ------------------------------------------------- |
| Phase 2 | v1.1 → v1.5 | One milestone per minor release; ~4-8 weeks each. |
| Phase 3 | v1.6 → v1.9 | One milestone per minor release; ~3-6 weeks each. |
| Phase 4 | v2.0        | All five milestones land together as v2.0.        |

## Versioning

- Phase 2 lands feature additions under v1.x (no breaking changes).
- Phase 3 is internal hardening - also v1.x.
- Phase 4 is allowed breaking API changes if needed (e.g. config schema cleanups);
  hence v2.0.

## Out of scope (explicit)

The following have been considered and intentionally rejected from this roadmap:

- **Cluster mode / multi-node replication.** OpenServiceBus stays single-node. Run
  the official Azure Service Bus if you need HA.
- **Schema registry / strongly-typed messages.** Not a broker concern.
- **Claim-check / large-message helpers.** SB's 256 KB / 1 MB limits are
  intentional; we won't paper over them.
- **Premium-tier partitioning semantics.** Beyond the test/dev sweet spot.
- **SQL Server persistence.** Use SQLite or the official emulator if you
  need SQL Server. Revisit if real demand surfaces.

---

## Tracking

Each milestone gets its own GitHub milestone + tracking issue once Phase 2 starts.
This document is the source of truth for ordering and scope - edit it (with a PR)
when priorities shift.
