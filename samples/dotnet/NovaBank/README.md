# NovaBank - a real-life OpenServiceBus example

> **This entire application was built as an example.** It is not a real bank and holds its
> state in memory - it exists to show, end to end, what a non-trivial event-driven app
> looks like against Azure Service Bus, and how to run and test that exact same app
> against OpenServiceBus instead. Copy whatever is useful.

An event-driven demo bank built **100% against `Azure.Messaging.ServiceBus`**. The app has
no reference to OpenServiceBus at all - whether it talks to a real Azure Service Bus
namespace or to a local OpenServiceBus broker is decided purely by the connection string.

It also serves as the project's toughest compatibility scenario: _does OpenServiceBus
behave like the real thing when a non-trivial app is pointed at it?_ (Spoiler: building it
found seven real broker bugs - all fixed in this repo, see the bottom of this file.)

## What the app does

```
POST /api/transfers ──► [nova-transfers]  queue    ── dup-detection (MessageId = Idempotency-Key)
                              │                       MaxDeliveryCount → $DeadLetterQueue
                              ▼
                        TransferWorker ──► debit/credit ──► publish outcome
                                                              │
POST /api/payments ───► [nova-payments]  queue    ── sessions ▼(SessionId = accountId, FIFO per account)
                              │              [nova-events] topic
                        PaymentWorker ────────►   ├── audit          (match-all)        → GET /api/audit
                            (broker-side          ├── fraud          (amount >= 10000)  → alerts + auto-freeze
                             scheduling)          └── notifications  (SQL IN-filter)    → customer inboxes
```

- **Transfers** are asynchronous: the API returns `202`, a queue worker settles the money
  movement, outcome events fan out over the topic. Retries are safe end to end - the
  `Idempotency-Key` header becomes the command's `MessageId`, and the queue's duplicate
  detection guarantees at-most-once execution even if the API re-sends.
- **Payments** ride a session-enabled queue (`SessionId` = account id): per-account FIFO,
  cross-account concurrency. Future-dated payments use `ScheduleMessageAsync` - the broker
  holds them, so they survive API restarts.
- **Fraud** is broker-side filtering: the subscription's SQL rule (`amount >= 10000`) means
  the fraud worker only ever sees large settled movements. ≥ 25 000 freezes the account
  automatically and publishes `account.frozen` back through the topic.
- **Poison messages**: send a transfer with `reference = "CHAOS"` and the worker throws on
  every delivery; after `MaxDeliveryCount` the broker dead-letters the command. Inspect it
  via `GET /api/admin/dead-letters/nova-transfers`.

Seeded data: customers `CUS-ALICE`, `CUS-BOB`, `CUS-ACME` with accounts `ACC-ALICE`
(€12 500), `ACC-BOB` (€850), `ACC-ACME` (€250 000).

## Run it locally against OpenServiceBus (Docker)

```bash
cd samples/dotnet/NovaBank
docker compose up -d --build      # broker + Explorer UI, entities bootstrapped from servicebus-config.json
dotnet run --project src/NovaBank.Api --launch-profile Local
```

- Swagger UI: <http://localhost:5080/swagger>
- OpenServiceBus Explorer: <http://localhost:5400> - watch queues/DLQs live while you click around

The compose file **builds the broker from this repo checkout** so it contains today's
fixes; swap `build: ..` for `image: mauritsarissen/openservicebus:latest` once they ship.

Demo script for Swagger:

1. `POST /api/transfers` - from `ACC-ACME` to `ACC-ALICE`, amount `30000`, currency `EUR`.
2. `GET /api/transfers/{id}` - watch it go `accepted → completed`.
3. `GET /api/fraud/alerts` - a `critical` alert; `GET /api/accounts/ACC-ACME` - now `frozen`.
4. `GET /api/customers/CUS-ACME/notifications` - "Account frozen" landed in the inbox.
5. `POST /api/transfers` with `reference = "CHAOS"`, then `GET /api/admin/dead-letters/nova-transfers`.
6. `POST /api/payments` with `executeAtUtc` ~30s in the future, watch it flip to `executed`.

## Run it against real Azure Service Bus

Fill in the connection string in `src/NovaBank.Api/appsettings.Azure.json` (the namespace
needs the same entities as `servicebus-config.json`: `nova-transfers` with duplicate
detection, `nova-payments` with sessions, `nova-events` + the three subscriptions), then:

```bash
dotnet run --project src/NovaBank.Api --launch-profile Azure
```

Nothing else changes - same code, same entity names.

### Config layering

| File                     | Purpose                                             |
| ------------------------ | --------------------------------------------------- |
| `appsettings.json`       | Entity names + logging. No connection string.       |
| `appsettings.Local.json` | Connection string → local OpenServiceBus container. |
| `appsettings.Azure.json` | Connection string → your Azure namespace.           |

The launch profiles (`Local` / `Azure` in `Properties/launchSettings.json`) just set
`ASPNETCORE_ENVIRONMENT`, which picks the overlay. `ServiceBus__ConnectionString` as an
environment variable works too (e.g. in CI or Kubernetes).

## Tests - the whole bank on an embedded broker

```bash
cd samples/dotnet/NovaBank
dotnet test
```

The suite has two layers, both running against an **embedded OpenServiceBus**
(`OpenServiceBusTestHost`, project-referenced straight from this repo's source). No
Docker, no Azure, ~15 seconds for everything.

**1. End-to-end API tests** (`TransferTests`, `FraudTests`, `PaymentTests`, …) boot the
real API via `WebApplicationFactory`; the only override is `ServiceBus:ConnectionString`.
Covered: transfer settlement + audit + both-sides notifications, idempotency under client
retries, insufficient funds, broker-side fraud filtering, automated account freeze,
immediate/scheduled/FIFO-ordered session payments, poison-message dead-lettering surfaced
through the admin API, and the Swagger document itself.

**2. Service-bus-level tests** (`tests/NovaBank.Api.Tests/Messaging/`) skip HTTP entirely
and talk to the broker with a raw `ServiceBusClient`, asserting on broker internals via
`host.Store`:

- `EventPublisherBusTests` - wire shape of published events (subject, content type,
  envelope round-trip, `amount` as a filterable property) and **subscription routing
  proven at the broker**: a 500 EUR movement reaches audit while
  `host.Store.CountAsync("nova-events/Subscriptions/fraud")` proves no copy was ever
  enqueued for fraud; a 1M _intent_ event carries no amount property so SQL-null semantics
  keep it out of fraud too.
- `TransferWorkerBusTests` - `TransferWorker` tested as a bare message consumer: commands
  injected with a raw sender; asserts settlement, business-failure-vs-poison semantics
  (business failures complete, poison retries to MaxDeliveryCount then dead-letters with
  `DeliveryCount` history intact - verified by _peeking the DLQ itself_), and that
  unknown-transfer commands are swallowed instead of redelivered forever.
- `BusSemanticsTests` - the raw broker guarantees NovaBank leans on: duplicate detection
  (two sends, one stored message, first-send-wins), interleaved multi-session FIFO, and a
  **time-travel test**: a payment scheduled 5 minutes out executes in milliseconds by
  advancing a `FakeTimeProvider` - the whole broker runs on the injected clock.
- `BusEdgeCaseTests` - session-less sends/schedules to the session queue are _rejected_
  (not black-holed), the payments queue provably has no dedup, the 10-minute dedup window
  slides (proven with a fake clock), and a malformed (non-JSON) command dead-letters
  instead of looping or vanishing.

**3. Edge-case suites** (`tests/NovaBank.Api.Tests/EdgeCases/`) - the long tail:

- `ApiValidationTests` - every synchronous rejection: missing fields, unknown ids (404s on
  both transfer legs), self-transfers, non-positive amounts, invalid currencies, negative
  opening balances, unknown DLQ names.
- `MoneyEdgeTests` - exact-balance transfers to zero, one cent over balance fails, one-cent
  transfers work, `0.1 + 0.1 + 0.1 == 0.3` (decimal, no float drift), two-billion transfers
  keep exact amounts.
- `FrozenAccountTests` - a 25k cash deposit freezes via the real fraud pipeline; then every
  money path is blocked: deposits/withdrawals 409, payments 409, and transfers _into_ the
  frozen account fail asynchronously with `destination_account_frozen`.
- `FraudBoundaryTests` - one cent either side of both thresholds (9 999.99 / 10 000 /
  24 999.99 / 25 000, both inclusive), plus large-withdrawal detection.
- `PaymentEdgeTests` - past `executeAtUtc` runs immediately, exact-balance payments,
  payee/currency/amount validation.
- `IdempotencyAndConcurrencyTests` - same key with a _different_ payload returns the
  original (first write wins), ten parallel posts with one key → exactly one 202 + one
  execution, five parallel transfers racing a 100-balance account → exactly three settle
  and the account can never go negative, ten parallel deposits all apply.

## Broker bugs this scenario caught (fixed in this repo)

1. **Scheduled messages on session queues never became visible** -
   `InMemoryMessageStore.ActivateScheduled` pushed activated messages onto the sessionless
   channel, so session receivers starved. Regression test:
   `ScheduledMessagesTests.ScheduleMessageAsync_OnSessionQueue_ActivatesIntoTheSessionChannel`.
2. **Peeked messages had no AMQP header** - reading `DeliveryCount` on any peeked message
   threw `Nullable object must have a value` (broke DLQ inspection tooling). Regression
   test: `PeekTests.PeekMessagesAsync_OnDeadLetterQueue_ExposesDeliveryCountAndDeadLetterReason`.
3. **Accept-next-session with no sessions returned a `GeneralError`** - real Service Bus
   answers `com.microsoft:timeout`, which `ServiceBusSessionProcessor` treats as a quiet
   poll; the custom error sent it into a hot retry/log-spam loop and slowed shutdown.
   Regression test: `SessionTests.AcceptNextSessionAsync_NoSessionsAvailable_SurfacesServiceTimeout`.
4. **Dead-lettered messages lost their delivery history** - the DLQ copy re-enqueued with
   `DeliveryCount = 0`; Azure keeps the count the message died with. `deliveryCount` now
   threads through `IMessageRouter.RouteAsync` / `IMessageStore.EnqueueAsync`.
5. **Graceful receiver/processor close hung for 60 seconds** - the SDK drains the link
   before closing, and `QueueReceiverSource.GetMessageAsync` blocked indefinitely on the
   store, so the drain never completed (`TimeoutException … for object drain`). Session
   links already had the drain-aware poll loop; queue links now use the same pattern.
   Regression tests: `DrainTests`. In a real app this was "shutdown takes a minute per
   receiver".
6. **Session-less sends to session queues were silently black-holed** - the broker accepted
   them and routed them to the sessionless channel no session receiver reads. Azure rejects
   with `amqp:not-allowed` (SDK: `InvalidOperationException`); the send, batch, and
   schedule paths now do the same. The schedule path had the mirror bug too: it honored
   `GroupId` even on _non_-session queues, stranding those messages in an unread session
   channel. Regression tests in `SessionTests`.
7. **Accept-next-session now parks server-side like Azure** - instead of rejecting
   instantly when no session exists, the broker holds the attach open until a session
   appears or the client's conveyed timeout (`com.microsoft:timeout` link property)
   elapses. Subtlety this surfaced: when a `ServiceBusSessionProcessor` recycles slots it
   _aborts_ superseded pending accepts locally - no detach ever reaches the broker - so a
   zombie parked link could win a session and strand it. A watchdog closes any
   parked-accept link that never starts pumping credit, releasing the session to a live
   waiter. Regression tests: `SessionTests` (parked pickup) and
   `SessionProcessorChurnTests` (zombie scenario).

That "declared subscription rules silently don't filter" trap is also worth knowing:
OpenServiceBus (like Azure) auto-creates a `$Default` match-all rule on every new
subscription, and declaring extra rules in `config.json` does **not** remove it - so name
your declarative rule `$Default` to replace it (this project's `servicebus-config.json`
does exactly that).
