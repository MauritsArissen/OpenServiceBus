# OpenServiceBus.Samples.QuickStart

Smallest possible end-to-end sample: a console app that sends three messages to a queue,
then receives and completes them. Useful as the first thing to run if you've never used
OpenServiceBus before.

## What it demonstrates

- Bringing up the broker via `docker compose`.
- Declarative queue bootstrap from a mounted `config.json` (the `quickstart` queue is
  ready the moment the broker starts).
- The plain `ServiceBusClient` + `Sender` + `Receiver` round-trip - identical to the
  shape you'd use against real Azure Service Bus.

## Run it

```bash
# Terminal 1: broker
docker compose up -d

# Terminal 2: console app
dotnet run

# Cleanup
docker compose down -v
```

Expected output:

```
Sending three messages to 'quickstart'…
  sent qs-001
  sent qs-002
  sent qs-003

Receiving from 'quickstart'…
  got id=qs-001 body="hello #1" seq=1
  got id=qs-002 body="hello #2" seq=2
  got id=qs-003 body="hello #3" seq=3

Done.
```

## Pointing at a different broker

Override the endpoint via env var:

```bash
SERVICEBUS_CONNECTION='Endpoint=sb://...;UseDevelopmentEmulator=true' dotnet run
```

## Files

| File                 | Purpose                                           |
| -------------------- | ------------------------------------------------- |
| `Program.cs`         | The send + receive loop                           |
| `config.json`        | Declares the `quickstart` queue at broker startup |
| `docker-compose.yml` | Broker with `config.json` mounted in              |
