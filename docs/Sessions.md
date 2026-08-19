# Sessions

Sessions give you **per-session FIFO** with **exclusive ownership**. Mark a queue or
subscription as `RequiresSession=true` and:

- Every sent message must carry a `SessionId` (AMQP `properties.group-id`).
- Receivers must claim a session lock before they can receive - `AcceptSessionAsync`.
- The broker only hands messages with the matching `SessionId` to the lock holder.
- Order within a session is strictly preserved; order across sessions is independent.

Optional per-session state (`SetSessionState` / `GetSessionState`) lets you persist
saga-style cursors keyed by session id.

## Create a session-enabled entity

```csharp
await host.Queues.CreateAsync(new QueueDescriptor
{
    Name = "sessioned",
    RequiresSession = true,
    LockDuration = TimeSpan.FromMinutes(1),
});
```

Subscriptions accept the same flag:

```csharp
await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
{
    TopicName = "events",
    Name = "by-tenant",
    RequiresSession = true,
});
```

Publishing to the topic with a `SessionId` fans out session-aware, per subscription:

- A **session-enabled** subscription stores its copy in per-session channels;
  `AcceptSessionAsync("events", "by-tenant", sessionId)` (or accept-next-session, or a
  `ServiceBusSessionProcessor`) delivers it under an exclusive session lock, with
  session state and lock renewal - exactly like a session queue.
- A **plain** sibling subscription on the same topic gets an ordinary copy, receivable
  by normal receivers (the `SessionId` property is still on the message).
- A message published **without** a session id can never be delivered by a
  session-enabled subscription; its copy is **dead-lettered** to that subscription's
  DLQ instead of being stranded. (The topic itself accepts the send - unlike a session
  queue, which rejects sessionless sends outright.)

## Send to a session

```csharp
var sender = client.CreateSender("sessioned");
await sender.SendMessageAsync(new ServiceBusMessage("first")  { SessionId = "tenant-A", MessageId = "1" });
await sender.SendMessageAsync(new ServiceBusMessage("second") { SessionId = "tenant-A", MessageId = "2" });
await sender.SendMessageAsync(new ServiceBusMessage("other")  { SessionId = "tenant-B", MessageId = "3" });
```

Sends without a `SessionId` against a session-enabled entity are rejected - Service Bus
parity.

## Accept a specific session

```csharp
var session = await client.AcceptSessionAsync("sessioned", "tenant-A");
var m1 = await session.ReceiveMessageAsync(); // MessageId "1"
var m2 = await session.ReceiveMessageAsync(); // MessageId "2"
await session.CompleteMessageAsync(m1);
await session.CompleteMessageAsync(m2);
```

While `session` is alive, the broker:

1. Holds an exclusive lock on `tenant-A` - other receivers calling
   `AcceptSessionAsync("sessioned", "tenant-A")` get `null` until this session disposes
   or the lock expires.
2. Filters deliveries to only messages with `SessionId == "tenant-A"`. Messages for
   `tenant-B` stay queued.

## Accept the next available session

Workers can grab whichever session has pending messages:

```csharp
var session = await client.AcceptNextSessionAsync("sessioned");
Console.WriteLine($"locked session = {session.SessionId}");
```

The broker picks the session with the **lowest unclaimed sequence number** that no one
else holds - preserves cross-session FIFO when multiple sessions arrive simultaneously.

Returns `null` if no unclaimed sessions have messages.

## Session lock + renewal

Session locks have the same duration as the entity's message lock (`LockDuration` on the
descriptor) and auto-expire on the deadline. Renew via:

```csharp
await session.RenewSessionLockAsync();
```

When the lock expires (lock duration with no renew), another receiver can grab the
session via `AcceptSessionAsync`. The broker emits `com.microsoft:session-cannot-be-locked`
or `com.microsoft:no-sessions-available` errors on contention - the SDK surfaces these as
`ServiceBusException` with the expected reason.

## Per-session state

```csharp
await session.SetSessionStateAsync(BinaryData.FromString("checkpoint-7"));

var blob = await session.GetSessionStateAsync();
Console.WriteLine(blob.ToString()); // "checkpoint-7"
```

State persists across receivers and lock expirations - it's queue-scoped + session-id-scoped,
not lock-scoped. Pass `null` to `SetSessionStateAsync` to clear.

## Listing sessions (`com.microsoft:get-message-sessions`)

The broker implements the full paging contract of the `com.microsoft:get-message-sessions`
`$management` operation, on queues and on subscriptions. This is the operation behind the
SDKs' session enumeration APIs (`ServiceBusClient.GetMessageSessionsAsync` in .NET,
`listMessageSessions` in Node.js, `listSessions` in Java, `list_queue_sessions` /
`list_subscription_sessions` in Python - all merged into the SDK main branches, pending
release at the time of writing).

Request body map fields:

- `last-updated-time` (timestamp) - `DateTime.MaxValue` is the "all live sessions"
  sentinel every SDK sends by default (encoded as the year-10000 millisecond value
  `253402300800000`, which decodes back to `DateTime.MaxValue` here). Any other value
  switches to filter mode. A missing field behaves like the sentinel.
- `skip` (int) - number of sessions to skip past, i.e. the page offset. Negative values
  are clamped to 0.
- `top` (int) - maximum page size. A missing `top` returns everything; a non-positive
  `top` returns an empty page (204), matching the service behavior the SDKs document.

Reply:

- `200 OK` with a body map carrying `sessions-ids` (array of strings) and `skip` (int) -
  the next page cursor, always `request skip + returned count`. Track 1 clients page on
  the returned `skip`; track 2 clients recompute it, both terminate correctly.
- `204 NoContent` with the `com.microsoft:session-not-found` error condition when no
  (more) sessions match - the SDKs treat this as the end of the enumeration.

Which sessions appear:

- **Default mode** (sentinel): sessions that currently have at least one available
  message (not deferred, not scheduled for the future, not locked by a receiver) OR a
  non-null stored session state.
- **Filter mode** (a real `last-updated-time`): only sessions whose stored session state
  was set or updated after that instant - the same semantics the SDKs document for their
  `sessionStateUpdatedAfter` parameter. Sessions that only have messages, or whose state
  was cleared to null, never match filter mode. On the SQLite store, state rows written
  by a broker version that predates the `updated_at` column carry no update instant and
  are likewise excluded from filter mode (they still list in default mode).

Results are ordered by session id (ordinal ascending) so skip-based paging is
deterministic and never loops. Like real Service Bus, paging indexes into a live view:
sessions added or removed between page requests can shift the offsets, so an enumeration
running concurrently with traffic may see duplicates or misses. The optional
`last-session-id` cursor some SDKs send is accepted and ignored - `skip` alone drives
paging.

## Wire-protocol details

- `AcceptSessionAsync` opens a receiver link with a `com.microsoft:session-filter` set to
  the session id. The broker echoes `com.microsoft:locked-until-utc` (as long-ticks, not
  DateTime) in the attach reply.
- The session-locked source uses an AMQP drain handshake - sessions always call
  `DrainAsync` even when no messages are available, so the receiver source watches
  `link.IsDraining` and exits the await cleanly.
- When the receiver detaches (clean or network drop) the broker auto-releases the session
  lock on the next sweep.

## Tests

- [`tests/OpenServiceBus.IntegrationTests/SessionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/SessionTests.cs)
  - full SDK coverage (FIFO, accept-next, session state, lock renewal).
- [`tests/OpenServiceBus.IntegrationTests/SessionEnumerationTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/SessionEnumerationTests.cs)
  - get-message-sessions end to end: mixed active/state-only sessions, paging past the
    SDK page size, last-updated filtering, session-enabled subscriptions.
- [`tests/OpenServiceBus.Amqp.WireTests/GetMessageSessionsWireTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.Amqp.WireTests/GetMessageSessionsWireTests.cs)
  - the raw wire contract (request fields, reply map, 204 terminator, defaults).
- [`tests/OpenServiceBus.InMemoryStorage.Tests/SessionsTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.InMemoryStorage.Tests/SessionsTests.cs)
  - store-level edge cases.

## See also

- [Topics and Subscriptions](Topics-and-Subscriptions) - `RequiresSession` on subscriptions.
- [Configuration](Configuration) - declaring session-enabled entities in `config.json`.
