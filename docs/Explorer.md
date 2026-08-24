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

## Canned messages

Save a fully configured Send form under a name and replay it later with one click.
An Explorer-only feature: applications using the SDKs directly are unaffected -
variables and environments exist purely in the Explorer's compose surface, resolved
before its own SDK send.

- **Save** - fill the Send tab (body, system properties, application properties, copies,
  strategy) and hit "Save as canned". A canned message is scoped to the entity it was
  saved on or to "any entity"; the Send tab's picker only offers the ones that apply to
  the selected entity.
- **Manage** - the sidebar's "Canned messages" entry (and the Send tab's Manage button)
  opens a master-detail management page: a row list of the library on the left, an
  INLINE editor on the right with every Send field, explicit Save/Revert with an
  unsaved-changes indicator, plus duplicate, delete and create-from-scratch - no
  modals. On phones the list comes first and a row opens the editor with a back
  button.
- **Import** - on the management page: a JSON array of canned messages (the export shape
  of the API's `GET /api/canned`). Imports merge by name: existing names are skipped by
  default, and the result toast offers a one-click "Replace existing".
- **Variable highlighting** - while composing (Send tab and the editor), body text like
  `{{$guid}}` is colored green when the built-in is valid, blue when an environment
  token resolves in the active environment, and amber with a wavy underline when
  unknown or malformed. Hovering a token shows its explanation or active value directly
  in the text; clicking one places the caret, double-clicking selects it. The
  "Variables" button opens the grouped guide with click-to-copy syntax.
- **Dynamic variables** - resolved at send time, independently for every copy of a
  multi-count send, in the body, MessageId, CorrelationId, Subject, ReplyTo, To,
  SessionId, PartitionKey and application property values. Names are case-insensitive
  and follow Postman where an equivalent exists:

  | Variable | Result |
  | -------- | ------ |
  | `{{$guid}}` | random guid, lowercase (also `{{$guid upper}}` / `{{$guid lower}}`) |
  | `{{$ulid}}` | time-sortable unique id - send order stays visible when peeking |
  | `{{$sequence}}` | incrementing counter, `{{$sequence 100}}` picks the start; scoped per entity + template, survives across sends until the Explorer restarts (the demo's 30-minute reset also restarts it) |
  | `{{$index}}` | zero-based copy index within a multi-count send (`count: 5` gives 0..4) |
  | `{{$datetime iso8601}}` | current UTC time; formats: `iso8601`, `rfc1123`, `unix`, `unixms` |
  | `{{$datetime 'yyyy-MM-dd'}}` | custom .NET format string, single-quoted (spaces allowed) |
  | `{{$datetime iso8601 -5d}}` | optional offset: `[+-]N` plus `y M w d h m s` (months are capital `M`) |
  | `{{$timestamp}}` | unix seconds shorthand |
  | `{{$randomInt}}` | random integer 0..1000, or `{{$randomInt min max}}` inclusive |
  | `{{$randomDouble min max}}` | random decimal in range; optional third argument = decimals (default 2) |
  | `{{$randomBoolean}}` | `true` or `false` |
  | `{{$randomAlphaNumeric n}}` | random letters+digits of length n; `{{$randomHex n}}` for hex |
  | `{{$randomChoice a\|b\|c}}` | one of the listed values - enums like regions or statuses |
  | `{{$randomBase64 bytes}}` | random blob of exactly N bytes, base64-encoded - payloads of a precise size |
  | `{{$repeat 'text' n}}` | deterministic padding: the quoted text repeated n times (capped at 1 MB) |

  A MessageId containing a variable is used as resolved - the usual `-0…-N` suffixing
  for multi-count sends is skipped because each copy is already unique. Unknown or
  malformed variables are left in the text verbatim, and pressing Send with any
  unresolvable variable (built-in or environment) opens a confirmation first: continue
  and send the token text verbatim, or cancel and fix it. In the composer, hovering a
  highlighted token explains it in place - built-ins show what they generate,
  environment tokens show the active environment's value - and the "Variables" guide
  lists the whole catalogue grouped by purpose, with click-to-copy syntax.
- **Library file (optional)** - point `OSB_EXPLORER_CANNED_FILE` at a JSON file and the
  library becomes team-shareable config you commit to git: the Explorer loads it at
  startup, every UI edit writes back to it (pretty-printed, stable order - clean git
  diffs), and the management page shows which file backs the library. The file IS the
  import/export format, so Export downloads exactly what would be committed. A read-only
  file (`:ro` docker mount) still loads, but edits stay in memory until restart - the
  management page flags that. A missing file starts empty and is created on the first
  save; an unreadable or invalid file logs a warning and starts empty instead of
  crashing. See [Docker](Docker) for the compose mount example.
- **Reload & reset** - `POST /api/canned/reset` (the management page's "Reload from
  file" button) re-reads the file as it is on disk right now, discarding unsaved
  session state - the way to pick up a `git pull` without restarting. Without a file
  the reset restores the startup state. In the hosted live demo (`OSB_EXPLORER_DEMO=true`)
  the Explorer also resets automatically on the same wall-clock cadence as the rest of
  the demo reset (`OSB_EXPLORER_RESET_INTERVAL_SECONDS`, default 30 minutes), so demo
  visitors always find a clean library. A normal Explorer never resets on its own.
- **Library storage** - without the file variable the library lives in the Explorer
  backend in-memory, shared by every browser connected to it - broker state is never
  involved, so it works identically with both storage modes and against real Azure.

## Environments

Postman's environments, applied to the broker: a named set of key/value pairs, one
active per browser, referenced in payloads as `{{key}}`.

- **Define** sets like `Card of Alice` (`cardnumber = 123400000`, `cardholder = alice`)
  and `Card of Bob`; a canned message writes
  `{ "cardnr": {{cardnumber}}, "cardholder": "{{cardholder}}" }` and switching the
  active environment in the topbar changes what the same send produces.
- **Namespace split** - plain `{{key}}` resolves from the active environment; `{{$...}}`
  stays reserved for the built-in dynamic variables, exactly like Postman. Environment
  resolution runs FIRST, so an environment value may itself contain `{{$guid}}` and
  still resolve per message copy. Disabled values never resolve; unresolved names are
  sent verbatim with a warning toast, and the composer highlights them: blue when the
  active environment resolves them (hover a chip to see the value), amber when not.
- **Applies everywhere variables apply**: body, MessageId, CorrelationId, Subject,
  ReplyTo, To, SessionId, PartitionKey, application property values - in the Send tab
  and canned messages alike. The active environment is a per-browser choice
  (localStorage); the library of environments is shared Explorer state.
- **Manage** via the sidebar's Environments entry: the same master-detail page as the
  canned library - environment rows on the left (active one marked), an inline editor
  on the right with per-value enable toggles and Save/Revert, plus set-active,
  duplicate, delete, import and export. The topbar's globe pill switches the active
  environment from anywhere. Import accepts Postman environment exports directly
  (extra Postman fields are ignored); export downloads the same shape.
- **File backing** - `OSB_EXPLORER_ENVIRONMENTS_FILE` works exactly like the canned
  message library file: loaded at startup, written back on edit, `Reload from file`
  (or `POST /api/environments/reset`) re-reads the disk, read-only mounts keep edits
  session-local, and the hosted demo resets both libraries on its 30-minute cadence.
  Out of scope by design: Postman's initial/current value split, secret masking, sync.

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

- [Canned Messages & Environments](Canned-Messages) - the full reference: library file
  workflow, environments, every dynamic variable, and the /api endpoints.
- [Configuration](Configuration) - every per-entity setting the modals expose.
- [Architecture](Architecture) - how the Explorer fits in the assembly graph.
