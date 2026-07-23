# OpenServiceBus.Samples.WorkerService

Shows the canonical .NET background-worker pattern: a `Microsoft.Extensions.Hosting`
`BackgroundService` that owns a `ServiceBusProcessor` and lets it manage concurrency,
lock renewal, and auto-complete on its own.

## What it demonstrates

- `Host.CreateApplicationBuilder` + `AddHostedService<OrderProcessor>()` - same wiring
  you'd use in production, just pointed at the emulator.
- `ServiceBusProcessor` with `MaxConcurrentCalls=4` and `PrefetchCount=16`.
- `AutoCompleteMessages=true` - clean return ⇒ complete, exception ⇒ abandon (auto-DLQ
  after `MaxDeliveryCount` retries).
- Graceful shutdown: the host's stop signal flows into `StopProcessingAsync`.

## Run it

```bash
# Terminal 1: broker
docker compose up -d

# Terminal 2: worker (run it for as long as you want)
dotnet run

# Terminal 3: send some test messages (use the Explorer or any Service Bus client)
dotnet run --project ../OpenServiceBus.Samples.QuickStart   # reuses the QuickStart sender
# …or send directly via the management REST API:
curl -X POST http://localhost:5300/queues/work/messages \
     -H 'Content-Type: application/json' -d '{"body":"hello"}' || true
```

Try sending a message with body `fail-this` - the worker throws on `fail` bodies,
demonstrating the auto-abandon → retry → auto-DLQ loop after 3 attempts.

## Files

| File                 | Purpose                                                                        |
| -------------------- | ------------------------------------------------------------------------------ |
| `Program.cs`         | Host builder + the `OrderProcessor` background service                         |
| `config.json`        | Declares the `work` queue with `MaxDeliveryCount=3` so retries hit DLQ quickly |
| `docker-compose.yml` | Broker with `config.json` mounted in                                           |

## See also

- [Architecture](../../docs/Architecture.md) - how the broker dispatches messages to consumers.
- [OpenTelemetry](../../docs/OpenTelemetry.md) - drop in an OTel exporter here to trace
  the worker's processing pipeline.
