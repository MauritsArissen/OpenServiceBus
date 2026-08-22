# System Properties

The broker stamps the same message annotations real Azure Service Bus stamps, so the SDK
system properties on `ServiceBusReceivedMessage` (and their equivalents in the Java,
Python, and Node.js SDKs) read real values instead of defaults.

## Broker-stamped annotations

Every delivery (regular receive, session receive, and receive-and-delete) carries:

| Annotation | SDK property (.NET) | Value |
| --- | --- | --- |
| `x-opt-sequence-number` | `SequenceNumber` | Sequence number on the entity the message is delivered from. |
| `x-opt-enqueue-sequence-number` | `EnqueuedSequenceNumber` | Sequence number assigned by the entity the message was ORIGINALLY sent to. |
| `x-opt-enqueued-time` | `EnqueuedTime` | UTC time the message landed on the entity. |
| `x-opt-locked-until` | `LockedUntil` | Peek-lock deadline (peek-lock deliveries). |
| `x-opt-message-state` | `State` | `Active` (0) on deliveries; see below. |

Peek stamps `x-opt-sequence-number`, `x-opt-enqueue-sequence-number`, `x-opt-enqueued-time`,
and `x-opt-message-state` (peeked messages carry no lock, so `x-opt-locked-until` is
absent). Deferred retrieval (receive-by-sequence-number) stamps the full delivery set,
including the fresh lock's `x-opt-locked-until`, with `x-opt-message-state = Deferred`;
like the real service it also embeds the lock token as the `x-opt-lock-token` DELIVERY
annotation on the returned message - the Python SDK settles deferred messages by that
annotation rather than by the response map's `lock-token` entry.

Dead-lettered messages additionally carry `x-opt-deadletter-source` - see
[Auto-Forwarding](Auto-Forwarding).

## Enqueued sequence number

`x-opt-enqueue-sequence-number` is the sequence number the message was assigned by the
entity the client originally sent it to. For a plain send to a queue it equals
`x-opt-sequence-number`. The two diverge whenever the broker re-enqueues the message
elsewhere, and the original value is preserved through every hop:

- **Auto-forwarding**: a send to a queue with `ForwardTo` mints the sequence number on the
  forwarding queue's own counter; the copy that lands on the target (after any chain of
  hops) keeps it, while `x-opt-sequence-number` is the target's fresh number. The same
  applies to a subscription's `ForwardTo`.
- **Topic fan-out**: every publish allocates one publish-side sequence number from the
  topic's counter; all subscription copies of that publish share it. Scheduled topic
  publishes use the same counter (the number returned by `ScheduleMessageAsync`), so the
  activated copies report the sequence number the sender was given.
- **Dead-lettering**: a message moved to a DLQ (or transfer DLQ) keeps the original
  entity's number.

The value is carried on the stored message (`StoredMessage.EnqueuedSequenceNumber`) so it
survives redeliveries, lock expiry, deferral, and - with the SQLite store - broker
restarts. See [Persistence](Persistence) for the schema details.

## Message state

`x-opt-message-state` matches `ServiceBusMessageState`:

- `Active` (0) - stamped on every normal delivery, and on peeked messages that are neither
  scheduled nor deferred.
- `Deferred` (1) - stamped on messages retrieved via receive-by-sequence-number and on
  peeked messages currently parked as deferred.
- `Scheduled` (2) - stamped on peeked messages whose activation time has not arrived.
  Scheduled messages are only observable via peek; once activated they deliver as `Active`.

## Partition keys

`x-opt-partition-key` and `x-opt-via-partition-key` round-trip from send to receive, peek
included: a sender-set `PartitionKey` / `TransactionPartitionKey` reads back with the same
value on the received message. The broker does not implement partitioning - the keys are
metadata carried with the message, exactly as sent, matching how the real service surfaces
them to receivers.

## Scheduled enqueue time

`x-opt-scheduled-enqueue-time` is preserved when a scheduled message activates: the
receiver of a formerly-scheduled message sees the original scheduled time in
`ScheduledEnqueueTime`. This holds for queue scheduling, sends stamped with the annotation,
and topic-held scheduled publishes that fan out at activation.
