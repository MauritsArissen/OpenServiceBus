# Explorer UI

A browser-based console for exploring queues + topics, sending and receiving messages,
managing rules, and watching dispositions during development.

```bash
# Terminal 1: broker
dotnet run --project src/OpenServiceBus.Host

# Terminal 2: Explorer
dotnet run --project src/OpenServiceBus.Explorer
```

Open <http://localhost:5400>. Entity CRUD goes through the real
`ServiceBusAdministrationClient` (the [ATOM management API](Admin-Client.md) on the AMQP
port) and messaging through the real `ServiceBusClient` - the JSON REST API (default
`http://localhost:5300`) is only used for the `/health` probe and metrics sampling.

## Layout

```
┌─────────────────────────────────────────────────────────────────────┐
│ OpenServiceBus / orders                          ● connected  ⚙ ☾  │
├─────────────┬───────────────────────────────────────────────────────┤
│ Search…     │  orders                              [Queue]          │
│             │  12 active · lock 60s · ttl ∞                         │
│ QUEUES   3  │                                                       │
│ • orders 12 │  Overview | Send | Receive (3) | Dead-letter | Metrics│
│   $DLQ    0 │  ─────────                                            │
│ • billing 0 │                                                       │
│             │  (selected tab content)                               │
│ TOPICS   2  │                                                       │
│ ▼ events    │                                                       │
│   ↳ all   5 │                                                       │
│   ↳ eu    3 │                                                       │
│ ▼ logs      │                                                       │
│             │                                                       │
│ ⚙ Connection│                                                       │
└─────────────┴───────────────────────────────────────────────────────┘
```

## Features

- **Entity tree** - queues + DLQ siblings, topics with collapsible subscription children
  and per-subscription DLQs. Live filter box. Click any entity to inspect.
- **Create dropdown** - modals for queue, topic, subscription with **every** feature flag
  exposed: lock duration, max-delivery, TTL, sessions, dedup + window,
  forward-to + forward-DLQ-to.
- **Overview tab** - full property dump, quick-action buttons (Send, Receive, Manage rules).
- **Send tab** - body editor, advanced fields (CorrelationId, Subject, ContentType,
  SessionId, PartitionKey, TTL, scheduled-for) and a custom application-properties editor
  (key/value rows). One click = real SDK send through the broker.
- **Receive tab** - peek-lock messages stay locked until you settle them. Each message
  shows id, sequence, delivery count, lock deadline, expires-at, dead-letter info,
  application properties. Disposition buttons grouped by intent: Complete (success),
  Abandon / Renew / Defer (neutral), DLQ (danger). Session ID input surfaces on
  session-enabled entities only.
- **Peek cursor** - browsing works the way the real Service Bus SDKs enumerate a queue:
  the first Peek anchors at the head, and a **Peek next** button continues from the last
  peeked sequence number + 1, appending each page to the list so you can walk an entire
  queue without receiving anything. Peek restarts from the head; Clear resets the browse
  list and the cursor. Peek returns active, locked, deferred and scheduled messages,
  matching Azure's documented browse semantics.
- **Multi-select + bulk actions** - every message row has a checkbox (shift-click selects
  a range, the header checkbox selects the whole page). With a selection active a bulk
  toolbar offers Complete / Abandon / Defer / Dead-letter (with a shared reason) on
  regular entities and Requeue / Delete on dead-letter queues. Bulk operations are not
  atomic: each lock is settled independently and the result reports
  `n succeeded / m failed` per batch, so one expired lock never aborts the rest. Bulk
  actions need locks, so they enable only when the selection holds locked messages; on
  phones the toolbar collapses into a dropdown. Settling a message (single or bulk)
  removes it from the list entirely, including any stale copy of it in the browse
  snapshot from an earlier peek.
- **Export** - download the selected messages (or the whole browsed list) as a JSON file:
  sequence number, ids, subject, timing, dead-letter metadata, application properties
  and body per message.
- **Dead-letter tab** (queues and subscriptions) - the same receive UI pointed at the
  entity's `$DeadLetterQueue`: peek or lock DLQ messages, **Requeue** a cleaned copy back
  to the parent (or through the topic so rules re-evaluate), or **Delete** it off the DLQ.
- **Resend** (DLQ and transfer-DLQ messages, single or bulk) - submit a brand-new copy of
  a dead-lettered message: same body, content type, subject, correlation id, session id,
  partition key, To/ReplyTo and application properties, but fresh broker metadata (new
  MessageId by default, delivery count 0, `DeadLetter*` markers stripped). The original
  always stays in the DLQ - removing it is a separate, explicit action. The dialog offers
  a destination picker (default: the source entity; for a subscription DLQ the topic, so
  rules re-evaluate) and a **Keep original MessageId** toggle for duplicate-detection
  entities - note the broker silently drops a kept id that is still inside the dedup
  window. Resend is peek-based, so it works on browsed messages without taking a lock.
  No Service Bus SDK has a resend API; this is the same peek-clone-send recipe the Azure
  portal and the community Service Bus Explorer implement, riding plain SDK calls, so it
  also works against a real Azure namespace.
- **Rules tab** (subscriptions only) - SQL / Correlation / True / False editor with
  examples in the help text. `$Default` rule visually distinguished from custom rules.
- **Purge** - a per-entity Purge button (queue + DLQ, topic across subscriptions,
  single subscription) and a sidebar purge-all action, both behind a confirm dialog.
  Purge is OpenServiceBus-native (real Service Bus has no purge API), so the buttons
  only enable when the connect ping's management probe identifies the broker as
  OpenServiceBus advertising the `purge` capability - see [Purge](Purge).
- **Metrics tab** - live throughput (new / completed / dead-lettered per interval) and
  message-count charts per entity, sampled every 15s while the Explorer runs, with a
  30 min - 24 h window selector. History is in-memory only and resets with the Explorer.
- **Auto-refresh** - topbar interval selector (off / 1-60s) that re-polls entity lists
  and counts. Message lists and rules refresh on explicit actions, not on the timer.
- **Responsive layout** - below tablet width the sidebar becomes an off-canvas drawer
  and toasts pop from the top of the screen instead of the bottom-right corner; the
  whole UI works on a phone.
- **Light/dark theme** with persisted preference.

## Connection panel

Bottom of the sidebar is a collapsible **Connection** drawer with the SDK connection
string and management URL. Persisted in localStorage so it survives refreshes.

The "Connect" button probes all three planes - `/health` (JSON REST), an SDK
`ServiceBusClient` construction (AMQP), and a real `GetNamespacePropertiesAsync` round
trip through the ATOM management API (`atom` line in the result panel).

## Architecture note

The Explorer is its own ASP.NET Core app (`OpenServiceBus.Explorer`) hosting a static HTML
page + a thin backend at `/api/*` that:

- Drives queue/topic/subscription/rule CRUD through the real
  `ServiceBusAdministrationClient` - the same ATOM management protocol every SDK admin
  client speaks, over the connection string's AMQP port.
- Translates "receive next" / "complete" / "abandon" / etc. into real Azure SDK calls
  against the broker over AMQP - so what you exercise in the UI is exactly what your
  production code would exercise.

This means the Explorer is **not** a peek window into broker internals; it's a real
client. The lock you take from the UI is a real peek-lock; abandoning it actually
increments delivery count; dead-lettering routes to the real DLQ.

## See also

- [Configuration](Configuration) - every per-entity setting the modals expose.
- [Architecture](Architecture) - how the Explorer fits in the assembly graph.
