# Samples

Every sample is **self-contained**: it ships a `docker-compose.yml` (to bring up the
broker with the right entities pre-declared), a `config.json` (the declarative bootstrap
the compose file mounts in), and a `README.md` explaining what it shows and how to run it.

## Picker

| Sample                                                                  | What it demonstrates                                                                            | When to look here                                             |
| ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| **[QuickStart](OpenServiceBus.Samples.QuickStart)**                     | Plain console send + receive against a Docker broker                                            | First time using OpenServiceBus                               |
| **[TopicsAndFilters](OpenServiceBus.Samples.TopicsAndFilters)**         | Topic pub-sub with SQL + correlation filter rules                                               | Building fan-out / pub-sub                                    |
| **[Sessions](OpenServiceBus.Samples.Sessions)**                         | Per-session FIFO with two parallel session-locked workers                                       | Tenant isolation, ordered per-key processing                  |
| **[WorkerService](OpenServiceBus.Samples.WorkerService)**               | `BackgroundService` + `ServiceBusProcessor` with concurrency + auto-DLQ                         | Production-shaped consumer code                               |
| **[Functions](OpenServiceBus.Samples.Functions)**                       | Minimal Azure Functions `ServiceBusTrigger`                                                     | Functions worker prerequisite check (and the integration test target) |
| **[FunctionsTriggerDemo](OpenServiceBus.Samples.FunctionsTriggerDemo)** | 5-trigger Functions app: peek-lock, batch, manual disposition, DLQ trigger, HTTP output binding | Exploring the full Functions binding surface                  |
| **[NovaBank](NovaBank)**                                                | Full event-driven banking API (Swagger) - dup-detected transfers, session payments, broker-side scheduling, SQL-filtered fraud/audit/notification fan-out, DLQ ops - plus a 79-test suite on the embedded broker | The complete real-app blueprint: architecture, config swapping (emulator ↔ Azure), and how to test all of it |

## Common pattern

Every sample follows the same flow:

```bash
cd samples/OpenServiceBus.Samples.<name>
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

## What's different per sample

The `config.json` and the C# code. Read each sample's README before running - they all
explain expected output in detail.

## See also

- **[`docs/`](../docs)** - full reference documentation for every feature these samples
  exercise (also mirrored to the [GitHub Wiki](https://github.com/mauritsarissen/OpenServiceBus/wiki)).
- **[`src/OpenServiceBus.Explorer`](../src/OpenServiceBus.Explorer)** - browser-based UI
  for poking at any of these brokers manually (`dotnet run` and open
  <http://localhost:5400>).
