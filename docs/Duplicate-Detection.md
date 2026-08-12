# Duplicate Detection

Enable `RequiresDuplicateDetection` on a queue or a topic and the broker silently drops
repeat sends with the same `MessageId` within a sliding time window. The sender sees a
normal "accepted" disposition either way - same observable behavior as Azure Service Bus.

## Enable it

```csharp
await host.Queues.CreateAsync(new QueueDescriptor
{
    Name = "deduped",
    RequiresDuplicateDetection = true,
    DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5),
});
```

Default window when null: **10 minutes** (matches Service Bus default).

Or in `config.json`:

```json
{
  "Name": "deduped",
  "Properties": {
    "RequiresDuplicateDetection": true,
    "DuplicateDetectionHistoryTimeWindow": "PT5M"
  }
}
```

## How it works

```csharp
var sender = client.CreateSender("deduped");
await sender.SendMessageAsync(new ServiceBusMessage("first")  { MessageId = "k" });
await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "k" }); // silently dropped
await sender.SendMessageAsync(new ServiceBusMessage("third")  { MessageId = "other" });

// Queue contains 2 messages: "first" (MessageId k) and "third" (MessageId other).
```

The second send returns the **original** `StoredMessage` to internal callers - so anything
tracking sequence numbers stays consistent. The wire-level disposition is still
`Accepted` - the SDK has no idea the dup was dropped.

After the window passes, the same `MessageId` is treated as a fresh send:

```csharp
await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "k" });

await Task.Delay(TimeSpan.FromMinutes(6)); // window expires

await sender.SendMessageAsync(new ServiceBusMessage("new")   { MessageId = "k" }); // accepted, new seq number
```

## Topics

Topics support duplicate detection too, with the same knobs
(`RequiresDuplicateDetection` + `DuplicateDetectionHistoryTimeWindow` on
`TopicDescriptor`, `CreateTopicOptions`, `config.json`, the REST API and the Explorer's
create-topic dialog). The semantics match Azure exactly:

- The check runs at the **topic, before fan-out** - one check per publish, not per
  subscription. A duplicate publish reaches **zero** subscriptions.
- Batched envelopes are checked per inner message, like the queue path.
- Scheduled publishes are deduplicated at **send time**, not at activation: scheduling a
  message reserves its `MessageId` immediately, so an identical id sent right afterwards
  is dropped even though the scheduled original has not activated yet.
- A repeated id **slides the window forward**.
- `RequiresDuplicateDetection` is immutable after creation - an update flipping it is
  rejected with a 400, like real Service Bus (the SDKs do not even expose a setter).
- The SQLite store persists the topic dedup history (`topic_dedup_history`) and the
  topic descriptor itself (`topic_descriptors`), so both the setting and the seen-id
  window survive a broker restart. Topic purge and topic deletion clear the history.

```csharp
await admin.CreateTopicAsync(new CreateTopicOptions("events")
{
    RequiresDuplicateDetection = true,
    DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
});
// Publish the same MessageId twice: every subscription receives exactly one copy.
```

## What counts as a `MessageId`?

Whatever AMQP carries in `properties.message-id`. The Azure SDK auto-generates one if you
don't set it - but if every message has a unique auto-generated id, dedup is a no-op.
For dedup to be meaningful, **the sender must set a deterministic `MessageId`** (e.g.
based on a business event id) so retries get the same value.

## Storage

- In-memory store: per-queue (and per-topic) dictionaries + a lazy sweep on each check.
  Cheap because expired entries get purged on the next dedup query.
- SQLite store: `dedup_history` (queues) and `topic_dedup_history` (topics), both indexed
  on `(name, expires_at)` and lazily swept before each dedup check. Written in the same
  transaction as the message so crash-safe dedup survives restarts.

## Tests

- [`tests/OpenServiceBus.IntegrationTests/DuplicateDetectionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/DuplicateDetectionTests.cs)
  - SDK-level queues: same-MessageId-twice drops the dup, batches, scheduled-at-send-time,
    update immutability.
- [`tests/OpenServiceBus.IntegrationTests/TopicDuplicateDetectionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/TopicDuplicateDetectionTests.cs)
  - SDK-level topics: exactly-one-per-subscription, window expiry via fake time, batches,
    scheduled publishes, admin round-trip, update immutability.
- [`tests/OpenServiceBus.InMemoryStorage.Tests/DuplicateDetectionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.InMemoryStorage.Tests/DuplicateDetectionTests.cs)
  and [`TopicDuplicateDetectionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.InMemoryStorage.Tests/TopicDuplicateDetectionTests.cs)
  - store-level: window expiry, sliding, delete/purge cleanup.
- [`tests/OpenServiceBus.SqliteStorage.Tests/SqliteMessageStoreTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.SqliteStorage.Tests/SqliteMessageStoreTests.cs)
  and [`SqliteTopicDedupTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.SqliteStorage.Tests/SqliteTopicDedupTests.cs)
  - same coverage against the persistent store, including restart survival.
- Every PR also runs three dedup smoke steps (queue, topic, batch) in all four official
  SDKs (.NET, Node.js, Java, Python) via `tests/sdk-smoke/`.
