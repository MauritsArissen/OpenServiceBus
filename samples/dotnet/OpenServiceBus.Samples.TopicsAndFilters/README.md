# OpenServiceBus.Samples.TopicsAndFilters

Topic pub-sub with **three subscriptions**, each filtering the same incoming stream
differently. Drives home that filters run server-side - the sender publishes one message
and the broker fans out to whichever subscriptions match.

## What it demonstrates

- One topic, three subscriptions: `all`, `eu-orders`, `high-priority`.
- A **SQL filter** (`region = 'eu' AND priority >= 5`).
- A **correlation filter** (matches `priority = 9` via application properties).
- A trivial subscription with the auto-installed `$Default` `TrueFilter` that catches everything.
- Server-side rule evaluation - the SDK sees a normal `ServiceBusSender.SendMessageAsync`,
  filtering happens inside the broker.

## Run it

```bash
docker compose up -d
dotnet run
docker compose down -v
```

Expected output:

```
Publishing 3 messages to topic 'events'…
  sent m1 region=eu priority=3
  sent m2 region=eu priority=9
  sent m3 region=us priority=9

Draining each subscription (timeout 2s)…

[all]
  m1  body="eu-low-priority"
  m2  body="eu-high-priority"
  m3  body="us-high-priority"

[eu-orders]
  m2  body="eu-high-priority"

[high-priority]
  m2  body="eu-high-priority"
  m3  body="us-high-priority"

Expected distribution:
  all            → m1, m2, m3
  eu-orders      → m2          (region=eu AND priority>=5)
  high-priority  → m2, m3      (priority=9 via correlation filter)
```

## Files

| File                 | Purpose                                                        |
| -------------------- | -------------------------------------------------------------- |
| `Program.cs`         | Sends three messages, drains each subscription, prints results |
| `config.json`        | Declares the topic, the three subscriptions, and their rules   |
| `docker-compose.yml` | Broker with `config.json` mounted in                           |

## See also

- [Topics and Subscriptions](../../docs/Topics-and-Subscriptions.md) - full SQL filter grammar reference.
