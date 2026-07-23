# Samples

Samples are organized **per language/SDK**. OpenServiceBus speaks real AMQP 1.0, so every
official Azure Service Bus client stack works against it - the [.NET samples](dotnet) are
the most complete today, and the layout leaves room for the other stacks:

```
samples/
  dotnet/    .NET (Azure.Messaging.ServiceBus) - 7 samples, see below
  node/      planned - @azure/service-bus
  java/      planned - azure-messaging-servicebus
  python/    planned - azure-servicebus
```

Until dedicated samples land for Node.js, Java, and Python, the
[`tests/sdk-smoke`](../tests/sdk-smoke) scripts double as minimal working examples for
each of those SDKs - each one shows connect, send, peek, receive/complete,
schedule/cancel, and session receive against a running broker.

## .NET samples

| Sample                                                                          | What it demonstrates                                                                            | When to look here                                             |
| -------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| **[QuickStart](dotnet/OpenServiceBus.Samples.QuickStart)**                       | Plain console send + receive against a Docker broker                                            | First time using OpenServiceBus                               |
| **[TopicsAndFilters](dotnet/OpenServiceBus.Samples.TopicsAndFilters)**           | Topic pub-sub with SQL + correlation filter rules                                               | Building fan-out / pub-sub                                    |
| **[Sessions](dotnet/OpenServiceBus.Samples.Sessions)**                           | Per-session FIFO with two parallel session-locked workers                                       | Tenant isolation, ordered per-key processing                  |
| **[WorkerService](dotnet/OpenServiceBus.Samples.WorkerService)**                 | `BackgroundService` + `ServiceBusProcessor` with concurrency + auto-DLQ                         | Production-shaped consumer code                               |
| **[Functions](dotnet/OpenServiceBus.Samples.Functions)**                         | Minimal Azure Functions `ServiceBusTrigger`                                                     | Functions worker prerequisite check (and the integration test target) |
| **[FunctionsTriggerDemo](dotnet/OpenServiceBus.Samples.FunctionsTriggerDemo)**   | 5-trigger Functions app: peek-lock, batch, manual disposition, DLQ trigger, HTTP output binding | Exploring the full Functions binding surface                  |
| **[NovaBank](dotnet/NovaBank)**                                                  | Full event-driven banking API (Swagger) - dup-detected transfers, session payments, broker-side scheduling, SQL-filtered fraud/audit/notification fan-out, DLQ ops - plus a 79-test suite on the embedded broker | The complete real-app blueprint: architecture, config swapping (emulator ↔ Azure), and how to test all of it |

## Common pattern (single-project samples)

```bash
cd samples/dotnet/OpenServiceBus.Samples.<name>
docker compose up -d           # broker with this sample's queues/topics pre-declared
dotnet run                     # or `func start` for the Functions samples
docker compose down -v         # cleanup (the -v wipes the volume)
```

The compose files all use `mauritsarissen/openservicebus:latest` and bind-mount
the sample's `config.json` to `/etc/openservicebus/config.json` inside the container. The
container reads it at startup and declares the queues + topics + rules described in the
sample's README.

**NovaBank** is the exception to the one-file pattern: it's a full solution
(`NovaBank.slnx` with an API project and a test project) built entirely as an example of a
real-world app. Its README carries its own run/test instructions.

## What's identical across samples

- **Broker image** - `mauritsarissen/openservicebus:latest`
- **Ports** - `5672` (AMQP) + `5300` (REST + `/health`)
- **Connection string** - the standard development-emulator shape:
  ```
  Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true
  ```
- **Storage** - SQLite at `/data/broker.db` on a named Docker volume.

## See also

- **[`docs/`](../docs)** - full reference documentation for every feature these samples
  exercise (also mirrored to the [GitHub Wiki](https://github.com/mauritsarissen/OpenServiceBus/wiki)).
- **[`tests/sdk-smoke/`](../tests/sdk-smoke)** - the cross-SDK smoke tests (.NET, Node.js,
  Java, Python) that gate CI and releases.
- **[`src/OpenServiceBus.Explorer`](../src/OpenServiceBus.Explorer)** - browser-based UI
  for poking at any of these brokers manually (`dotnet run` and open
  <http://localhost:5400>).
