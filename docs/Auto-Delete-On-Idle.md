# Auto-Delete On Idle

Queues, topics, and subscriptions can delete themselves after a configurable stretch of
inactivity, mirroring Service Bus's `AutoDeleteOnIdle`. Null (the default) means never;
the minimum is **5 minutes**, and lower values are rejected at create/update with the
same validation error shape as real Service Bus.

## What counts as activity

Sends (including fan-out copies landing on a subscription), successful receives, peeks,
and link attaches all reset the entity's idle clock. The clock starts at creation. When
the configured window elapses with none of these, a background sweeper deletes the
entity: a queue goes together with its dead-letter queue and messages, a subscription
with its backing queue, a topic with all of its subscriptions.

The sweeper runs on the broker's `TimeProvider`, so `OpenServiceBusTestHost` with a
`FakeTimeProvider` can time-travel the window:

```csharp
var clock = new FakeTimeProvider();
await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
await host.Queues.CreateAsync(new QueueDescriptor { Name = "ephemeral", AutoDeleteOnIdle = TimeSpan.FromMinutes(10) });

clock.Advance(TimeSpan.FromMinutes(11));
// "ephemeral" is deleted; GetQueueAsync now throws MessagingEntityNotFound.
```

## Setting it

```csharp
await admin.CreateQueueAsync(new CreateQueueOptions("ephemeral")
{
    AutoDeleteOnIdle = TimeSpan.FromMinutes(10),
});
```

```json
{ "Name": "ephemeral", "Properties": { "AutoDeleteOnIdle": "PT10M" } }
```

The value round-trips through the ATOM management API, the JSON REST API
(`autoDeleteOnIdle`), `config.json` (ISO-8601 duration), the Explorer (visible as a
badge and settable in the create dialogs), and persists across restarts with the SQLite
store.
