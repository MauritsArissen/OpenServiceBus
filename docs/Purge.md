# Purging Messages

Purge removes every message from an entity while keeping the entity itself - its
settings, its subscriptions and rules, and any live senders, receivers, or processors.
It exists for one scenario: a long-lived broker (for example an Aspire AppHost booted
once per test assembly) that needs an instant, side-effect-free reset between test cases,
where deleting and recreating entities is too slow and disturbs attached listeners.

Real Azure Service Bus has **no purge API** - even Microsoft's Service Bus Explorer
"purges" by receive-deleting messages client-side. Purge is therefore the first
deliberately emulator-native OpenServiceBus feature: it lives on the emulator's own JSON
management API (and the Explorer and Testing host), not on the ATOM plane that
`ServiceBusAdministrationClient` speaks.

## What a purge removes

- Active, locked, scheduled, and deferred messages
- Dead-letter queue contents (unless dead-lettering is forwarded to another entity, in
  which case that entity holds the messages and is purged separately)
- Session state blobs (`SetSessionState`)
- Duplicate-detection history, so a MessageId from before the purge lands again as a
  fresh message

## What a purge keeps

- The entity and all of its settings, subscriptions, and rules
- Live AMQP links: attached processors and receivers keep running and simply observe an
  empty entity
- Held session locks: a `ServiceBusSessionReceiver` keeps its exclusivity and receives
  the next message sent to its session
- In-flight settlements: completing/abandoning a message that was purged mid-flight is a
  quiet no-op, mirroring the broker's lock-lost behavior

## HTTP API (JSON management port, default 5300)

| Route | Effect |
| --- | --- |
| `DELETE /queues/{name}/messages` | Purge a queue and its dead-letter queue |
| `DELETE /queues/{name}/messages?subqueue=deadletter` | Purge only the dead-letter queue |
| `DELETE /topics/{name}/messages` | Purge every subscription of the topic (backing queues + their DLQs) |
| `DELETE /topics/{topic}/subscriptions/{name}/messages` | Purge one subscription (+ its DLQ); also accepts `?subqueue=deadletter` |
| `POST /purge` | Purge everything on the broker |

Responses are `200 {"purged": n}` (the global purge adds `"entities": k`), `404` for
unknown entities. Example between-test reset:

```bash
curl -X POST http://localhost:5300/purge
```

## OpenServiceBusTestHost

```csharp
await host.PurgeAllAsync();                     // whole broker
await host.PurgeQueueAsync("orders");           // queue + DLQ
await host.PurgeTopicAsync("events");           // all subscriptions
await host.PurgeSubscriptionAsync("events", "audit");
```

Each entity-scoped helper returns the number of messages removed, or `null` when the
entity does not exist.

## Explorer

Every queue, topic, and subscription card has a Purge button, and the sidebar has a
purge-all action. Because purge is emulator-native, the Explorer first checks what it is
connected to: on connect it reads the management API root (`GET /`), which identifies
the broker (`"name": "OpenServiceBus"`) and advertises `"capabilities": ["purge"]`. The
buttons stay disabled when the target does not positively identify as an OpenServiceBus
broker with the purge capability - for example a real Azure namespace or an older
emulator release.
