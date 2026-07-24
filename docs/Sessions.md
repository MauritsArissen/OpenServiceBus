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

> ⚠️ **Known limitation:** session routing is not yet threaded through topic fan-out.
> A `RequiresSession` subscription is accepted at creation time, but messages published
> to the topic reach its backing queue through the sessionless path, so session receivers
> on that subscription won't see them. Full session support currently applies to queues;
> use a queue (or forward the subscription to a session-enabled queue) for session
> semantics today.

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

`GetMessageSessionsAsync` lists every session id on the queue that has at least one
unprocessed message OR stored state.

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
- [`tests/OpenServiceBus.InMemoryStorage.Tests/SessionsTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.InMemoryStorage.Tests/SessionsTests.cs)
  - store-level edge cases.

## See also

- [Topics and Subscriptions](Topics-and-Subscriptions) - `RequiresSession` on subscriptions.
- [Configuration](Configuration) - declaring session-enabled entities in `config.json`.
