<p align="center">
  <img src="docs/assets/logo.png" alt="OpenServiceBus" width="440" />
</p>

<p align="center">
  <a href="https://github.com/mauritsarissen/OpenServiceBus/actions/workflows/ci.yml"><img src="https://github.com/mauritsarissen/OpenServiceBus/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://www.nuget.org/packages/OpenServiceBus.Testing"><img src="https://img.shields.io/nuget/v/OpenServiceBus.Testing.svg?logo=nuget&label=nuget.org" alt="NuGet" /></a>
  <a href="https://hub.docker.com/r/mauritsarissen/openservicebus"><img src="https://img.shields.io/badge/Docker-mauritsarissen%2Fopenservicebus-2496ED?logo=docker" alt="Docker Hub" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" /></a>
</p>

> A zero-dependency, embeddable **Azure Service Bus emulator** for .NET. Speaks real AMQP 1.0
> over TCP **and** WebSocket so `Azure.Messaging.ServiceBus`, Azure Functions `ServiceBusTrigger`,
> and any other Service Bus client connect unmodified.

---

## Why this exists

Microsoft ships an official Service Bus emulator, but it needs Docker + SQL Server and is
EULA-gated. OpenServiceBus is the MIT-licensed alternative: a single-node Service Bus
emulator that runs as a NuGet inside your test fixture, as a `docker run`, or as a
standalone executable - no SQL Server, no licensing dance, no 5-minute container boot.

It implements the full Service Bus feature surface that real client code uses: **queues,
topics + subscriptions (SQL/correlation filters), sessions, duplicate detection,
auto-forwarding, transactions, defer, scheduled messages, TTL, peek-lock with renewal,
dead-letter routing**, exposed over both **plain AMQP** and **AMQP-over-WebSocket**, with
native **OpenTelemetry** instrumentation throughout. Entity CRUD works through the real
**`ServiceBusAdministrationClient`** too - the broker serves the ATOM management API on the
same port as AMQP, so one connection string drives both planes.

|                          | OpenServiceBus                         | Official Microsoft emulator |
| ------------------------ | -------------------------------------- | --------------------------- |
| License                  | MIT                                    | EULA-gated                  |
| Embeddable in tests      | ✅ `OpenServiceBus.Testing` NuGet      | ❌                          |
| Container size           | ~300 MB Alpine                         | ~2 GB                       |
| Persistence              | In-memory or SQLite (single file)      | SQL Server (required)       |
| Transports               | AMQP-TCP + AMQP-over-WebSocket         | AMQP-TCP only               |
| Telemetry                | Native OpenTelemetry tracing + metrics | None                        |
| `config.json` compatible | ✅                                     | ✅                          |
| `ServiceBusAdministrationClient` | ✅ ATOM management API on the AMQP port | ❌                   |

---

## Install

### From source

```bash
git clone https://github.com/mauritsarissen/OpenServiceBus
cd OpenServiceBus
dotnet run --project src/OpenServiceBus.Host
```

The host binds `amqp://localhost:5672` (Service Bus SDK) and `http://localhost:5300`
(REST management + `/health`).

### Docker

```bash
docker run -d --name openservicebus \
  -p 5672:5672 \
  -p 5300:5300 \
  -p 5400:5400 \
  -v osb-data:/data \
  mauritsarissen/openservicebus:latest
```

| Port   | What                                                                  |
| ------ | --------------------------------------------------------------------- |
| `5672` | AMQP (use this in the Azure SDK connection string)                    |
| `5300` | REST management API + `/health`                                       |
| `5400` | Explorer browser UI - open <http://localhost:5400>                    |
| `5673` | AMQP-over-WebSocket (when `OPENSERVICEBUS__WEBSOCKETS__ENABLED=true`) |

The image runs the broker **and** the Explorer UI side-by-side. SQLite-backed at
`/data/broker.db` - mount the named volume and queues + messages survive container
recreates. See [Docker](docs/Docker.md) for the compose recipe, every env var, and the
WebSocket-transport setup.

### Connect

The same connection string works against either install path:

```text
Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true
```

```csharp
await using var client = new ServiceBusClient(
    "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true");

await client.CreateSender("orders").SendMessageAsync(new ServiceBusMessage("hello"));
var msg = await client.CreateReceiver("orders").ReceiveMessageAsync();
```

`UseDevelopmentEmulator=true` tells the Azure SDK to skip TLS and accept the broker on
plain TCP - the same trick the official Microsoft emulator uses.

The same connection string also drives the management plane
(`Azure.Messaging.ServiceBus` 7.20.1+):

```csharp
var admin = new ServiceBusAdministrationClient(connectionString);
await admin.CreateQueueAsync(new CreateQueueOptions("orders") { MaxDeliveryCount = 5 });
```

→ [SDK Admin Client](docs/Admin-Client.md)

---

## As a NuGet inside your tests

If you don't want any external process at all, install the test fixture and run the broker
inside your test process:

```bash
dotnet add package OpenServiceBus.Testing
```

Published on [nuget.org](https://www.nuget.org/packages?q=OpenServiceBus) - no extra source
needed. (GitHub Packages also mirrors every release, see
[Contributing](docs/Contributing.md#releasing).)

```csharp
await using var host = await OpenServiceBusTestHost.StartAsync();
await host.CreateQueueAsync("orders");

await using var client = new ServiceBusClient(host.ConnectionString);
await client.CreateSender("orders").SendMessageAsync(new ServiceBusMessage("hello"));

var receiver = client.CreateReceiver("orders");
var msg = await receiver.ReceiveMessageAsync();
await receiver.CompleteMessageAsync(msg);
```

One disposable host, free ephemeral port, full AMQP semantics - run thousands of these in
parallel. See [Testing](docs/Testing.md) for time-travel, parity testing, and the options
surface.

---

## Feature recipes

### Topics + filters (SQL / correlation / true / false)

```csharp
await host.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
    { TopicName = "events", Name = "eu" });
await host.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
{
    TopicName = "events", SubscriptionName = "eu", Name = "EuOnly",
    Filter = new SqlFilter("region = 'eu' AND priority >= 5"),
});

await client.CreateSender("events").SendMessageAsync(new ServiceBusMessage("hi-eu")
{
    ApplicationProperties = { ["region"] = "eu", ["priority"] = 7 }
});
```

→ [Topics and Subscriptions](docs/Topics-and-Subscriptions.md)

### Sessions

```csharp
await host.Queues.CreateAsync(new QueueDescriptor { Name = "sessioned", RequiresSession = true });
var sender = client.CreateSender("sessioned");
await sender.SendMessageAsync(new ServiceBusMessage("a") { SessionId = "S" });
await sender.SendMessageAsync(new ServiceBusMessage("b") { SessionId = "S" });

var session = await client.AcceptSessionAsync("sessioned", "S");
var first  = await session.ReceiveMessageAsync(); // "a"
var second = await session.ReceiveMessageAsync(); // "b"
```

→ [Sessions](docs/Sessions.md)

### Transactions (`TransactionScope`)

```csharp
using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
{
    await sender.SendMessageAsync(new ServiceBusMessage("a"));
    await receiver.CompleteMessageAsync(previouslyReceivedMessage);
    scope.Complete(); // commit; without this everything rolls back
}
```

→ [Transactions](docs/Transactions.md)

### Auto-forwarding

```csharp
await host.Queues.CreateAsync(new QueueDescriptor { Name = "downstream" });
await host.Queues.CreateAsync(new QueueDescriptor
    { Name = "ingress", ForwardTo = "downstream" });
// Sends to "ingress" land on "downstream" - invisible to senders.
```

→ [Auto-Forwarding](docs/Auto-Forwarding.md)

### Duplicate detection

```csharp
await host.Queues.CreateAsync(new QueueDescriptor
{
    Name = "deduped",
    RequiresDuplicateDetection = true,
    DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5),
});

await sender.SendMessageAsync(new ServiceBusMessage("first")  { MessageId = "k" });
await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "k" }); // silently dropped
```

→ [Duplicate Detection](docs/Duplicate-Detection.md)

### Declarative `config.json` bootstrap

OpenServiceBus reads the same `config.json` format as the official Microsoft emulator:

```json
{
  "UserConfig": {
    "Namespaces": [
      {
        "Name": "demo",
        "Queues": [
          {
            "Name": "orders",
            "Properties": {
              "LockDuration": "PT1M",
              "MaxDeliveryCount": 3,
              "RequiresSession": false,
              "DefaultMessageTimeToLive": "PT1H"
            }
          }
        ]
      }
    ]
  }
}
```

Resolution: `--config <path>` → `OPENSERVICEBUS_CONFIG` env var → `config.json` in the
host's content-root. Full schema in [Configuration](docs/Configuration.md).

### Persistence (SQLite)

```bash
export OPENSERVICEBUS__STORAGE__MODE=Sqlite
export OPENSERVICEBUS__STORAGE__DATASOURCE=/data/broker.db
dotnet run --project src/OpenServiceBus.Host
```

→ [Persistence](docs/Persistence.md)

### AMQP-over-WebSocket

```bash
export OPENSERVICEBUS__WEBSOCKETS__ENABLED=true
dotnet run --project src/OpenServiceBus.Host
```

```csharp
await using var client = new ServiceBusClient(
    "Endpoint=sb://localhost:5673;...;UseDevelopmentEmulator=true",
    new ServiceBusClientOptions { TransportType = ServiceBusTransportType.AmqpWebSockets });
```

→ [WebSocket Transport](docs/WebSocket-Transport.md)

### OpenTelemetry

```csharp
services.AddOpenTelemetry()
    .WithTracing(b => b.AddSource(OpenServiceBusDiagnostics.SourceName).AddOtlpExporter())
    .WithMetrics(b => b.AddMeter(OpenServiceBusDiagnostics.SourceName).AddOtlpExporter());
```

→ [OpenTelemetry](docs/OpenTelemetry.md)

---

## Configuration

The host honors these `appsettings.json` / environment variable keys:

| Key                                  | Default    | Notes                                                               |
| ------------------------------------ | ---------- | ------------------------------------------------------------------- |
| `OpenServiceBus:Amqp:Port`           | `5672`     | AMQP + ATOM management port                                         |
| `OpenServiceBus:Amqp:RequireSasAuth` | `false`    | Validate `$cbs put-token` and management requests against `SasKeys` |
| `OpenServiceBus:AtomManagement:Enabled` | `true`  | Serve the `ServiceBusAdministrationClient` API on the AMQP port     |
| `OpenServiceBus:Storage:Mode`        | `InMemory` | `Sqlite` to persist via the SQLite store                            |
| `OpenServiceBus:Storage:DataSource`  | `:memory:` | Path to the SQLite `.db` file (use `/data/broker.db` in containers) |
| `OpenServiceBus:WebSockets:Enabled`  | `false`    | Start the AMQP-over-WebSocket bridge                                |
| `OpenServiceBus:WebSockets:Port`     | `5673`     | WebSocket bridge port                                               |
| `OPENSERVICEBUS_CONFIG`              | -          | Path to a `config.json` for declarative bootstrap                   |

Full reference: [Configuration](docs/Configuration.md).

---

## Samples

Each sample is self-contained: it ships a `docker-compose.yml`, a `config.json`, and a
`README.md` that explains what it demonstrates and how to run it.

- **[`samples/dotnet/OpenServiceBus.Samples.QuickStart`](samples/dotnet/OpenServiceBus.Samples.QuickStart)** - minimal console send/receive against an emulator container.
- **[`samples/dotnet/OpenServiceBus.Samples.TopicsAndFilters`](samples/dotnet/OpenServiceBus.Samples.TopicsAndFilters)** - pub-sub with SQL + correlation filter rules.
- **[`samples/dotnet/OpenServiceBus.Samples.Sessions`](samples/dotnet/OpenServiceBus.Samples.Sessions)** - session-locked workers with per-session FIFO ordering.
- **[`samples/dotnet/OpenServiceBus.Samples.WorkerService`](samples/dotnet/OpenServiceBus.Samples.WorkerService)** - `Microsoft.Extensions.Hosting` background-worker pattern.
- **[`samples/dotnet/OpenServiceBus.Samples.Functions`](samples/dotnet/OpenServiceBus.Samples.Functions)** - minimal Azure Functions `ServiceBusTrigger` app (the integration-test target).
- **[`samples/dotnet/OpenServiceBus.Samples.FunctionsTriggerDemo`](samples/dotnet/OpenServiceBus.Samples.FunctionsTriggerDemo)** - interactive multi-trigger Functions app driven via the Explorer or HTTP.
- **[`samples/dotnet/NovaBank`](samples/dotnet/NovaBank)** - a complete event-driven banking API built 100% against `Azure.Messaging.ServiceBus` (Swagger UI, dup-detected transfers, session payments, scheduled standing orders, SQL-filtered fraud/audit/notification fan-out, DLQ inspection) with a 79-test suite running the whole app on the embedded test host. Made entirely as an example of a real-world integration.

See [`samples/README.md`](samples/README.md) for the full index and a quick chooser table.

---

## Documentation

Multi-page guides in [`docs/`](docs/) - also published to the [GitHub Wiki](https://github.com/mauritsarissen/OpenServiceBus/wiki):

- **[Getting Started](docs/Getting-Started.md)** - install + first send/receive
- **[Configuration](docs/Configuration.md)** - `config.json`, env vars, every option
- **[Architecture](docs/Architecture.md)** - assemblies, AMQP layer, store contracts
- **[Docker](docs/Docker.md)** - image, compose, env vars, persistence
- **[Persistence](docs/Persistence.md)** - SQLite store, schema, restart semantics
- **[SDK Admin Client](docs/Admin-Client.md)** - `ServiceBusAdministrationClient`, ATOM API, one-port design
- **[Topics and Subscriptions](docs/Topics-and-Subscriptions.md)** - filters, rules, fan-out
- **[Sessions](docs/Sessions.md)** - session-locked receivers, state, ordering
- **[Auto-Forwarding](docs/Auto-Forwarding.md)** - `ForwardTo`, DLQ forwarding, cycles
- **[Transactions](docs/Transactions.md)** - coordinator, `TransactionScope`, semantics
- **[Duplicate Detection](docs/Duplicate-Detection.md)** - window, observed behavior
- **[WebSocket Transport](docs/WebSocket-Transport.md)** - bridge setup, ports, client config
- **[OpenTelemetry](docs/OpenTelemetry.md)** - source names, attributes, gauges
- **[Explorer UI](docs/Explorer.md)** - running the browser console
- **[Testing](docs/Testing.md)** - `OpenServiceBusTestHost`, fake-time, parity tests
- **[Contributing](docs/Contributing.md)** - repo layout, conventions, releasing
- **[Roadmap](docs/ROADMAP.md)** - what's next

---

## Development

```bash
dotnet build         # multi-targets net8.0 + net10.0
dotnet test          # full regression suite (~210 tests)
docker build -t openservicebus:dev .
```

The Azure Functions integration test requires `func` (Azure Functions Core Tools v4)
and the .NET 8 runtime; it skips when either is missing. See [Contributing](docs/Contributing.md)
for repo conventions, the release flow, and how to add a new milestone.

---

## License

[MIT](LICENSE) © Maurits Arissen and OpenServiceBus contributors.
