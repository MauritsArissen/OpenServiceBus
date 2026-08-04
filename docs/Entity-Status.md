# Entity Status

Queues, topics, and subscriptions carry an operational status, mirroring Service Bus's
`EntityStatus`: `Active` (default), `Disabled`, `SendDisabled`, `ReceiveDisabled`.
Operators use it to freeze or drain entities without deleting them.

## Semantics

| Status | Sends | Receives | Typical use |
| --- | --- | --- | --- |
| `Active` | yes | yes | normal operation |
| `SendDisabled` | rejected | yes | drain: stop intake, keep consuming |
| `ReceiveDisabled` | yes | rejected | freeze delivery: keep banking messages |
| `Disabled` | rejected | rejected | full stop; content is preserved |

Rejected operations surface in the SDKs as `ServiceBusException` with reason
`MessagingEntityDisabled` (AMQP condition `com.microsoft:entity-disabled`). Enforcement
applies at link attach AND per transfer, so flipping the status also affects senders and
receivers that connected while the entity was still active; receivers that are already
attached simply stop being fed while receive is disabled and resume when re-enabled.
Scheduling a message counts as a send.

Per-entity specifics:

- **Queues**: as in the table. The dead-letter sub-queue is not affected by the parent's
  status, so DLQ inspection keeps working on a disabled queue.
- **Topics**: `Disabled`/`SendDisabled` reject publishes. `ReceiveDisabled` has no
  publish-side meaning on a topic and behaves like `Active`; disable receiving per
  subscription instead.
- **Subscriptions**: `Disabled`/`ReceiveDisabled` reject receivers but fan-out copies
  still land (a later drain loses nothing). `SendDisabled` stops new copies entering the
  subscription during fan-out; other subscriptions on the topic are unaffected.
- Auto-forward chains do not yet check the status of mid-chain targets - that behavior
  belongs to the transfer dead-letter queue work.
- Peek/browse and settling already-locked messages stay allowed in every status, so
  frozen entities remain inspectable.

## Setting the status

Every management surface round-trips it:

```csharp
// SDK admin client (ATOM management API)
QueueProperties queue = await admin.GetQueueAsync("orders");
queue.Status = EntityStatus.SendDisabled;
await admin.UpdateQueueAsync(queue);
```

```json
// config.json - Properties block of a queue, topic, or subscription
{ "Name": "orders", "Properties": { "Status": "ReceiveDisabled" } }
```

The JSON REST API (`/queues`, `/topics`, ... on port 5300) accepts and reports a
`status` field, the Explorer lists it, and `OpenServiceBusTestHost` takes it on any
descriptor. With the SQLite store, status - along with the rest of the queue descriptor -
survives a broker restart.
