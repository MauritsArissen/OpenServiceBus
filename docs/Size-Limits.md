# Message Size Limits and Entity Quotas

The broker enforces the two size ceilings real Service Bus enforces, with standard-tier
defaults, so oversized payloads fail locally the same way they would in Azure.

## Message size (`MaxMessageSizeInKilobytes`, default 256)

Queues and topics advertise their limit as `max-message-size` on every link attach. That
has two effects:

- The SDKs reject oversized sends with `ServiceBusException` reason
  `MessageSizeExceeded` (AMQP `amqp:link:message-size-exceeded`), usually client-side
  before the bytes ever leave the process - exactly like Azure.
- `ServiceBusMessageBatch` sizes itself from the link limit, so `TryAddMessage` returns
  false at the same boundary the real service uses.

The broker also enforces the limit per transfer server-side, and subscription receivers
inherit the parent topic's limit so large messages accepted by a roomy topic can be
delivered. Raise the limit per entity (any value; Azure premium allows up to 100 MB):

```csharp
await admin.CreateQueueAsync(new CreateQueueOptions("blobs") { MaxMessageSizeInKilobytes = 10240 });
```

## Entity quota (`MaxSizeInMegabytes`, default 1024)

Each queue tracks the bytes it currently stores (active, locked, scheduled, deferred -
including its dead-letter queue); a topic's usage covers all its subscription backing
queues. A send that would push usage past the quota is rejected with reason
`QuotaExceeded` (AMQP `amqp:resource-limit-exceeded`); receiving and settling messages
frees space and sends succeed again. Scheduling counts as a send.

Unlike Azure (1-5 GB standard), any positive quota is accepted, so tests can use tiny
values like `MaxSizeInMegabytes = 1` to exercise full-entity behavior quickly.

## Configuration

Both properties round-trip through the ATOM management API
(`ServiceBusAdministrationClient`), the JSON REST API, `config.json`
(`"MaxSizeInMegabytes": 2048`, `"MaxMessageSizeInKilobytes": 512` in a queue or topic
`Properties` block), Testing-host descriptors, and SQLite descriptor snapshots. The
Explorer shows each entity's current size against its quota.
