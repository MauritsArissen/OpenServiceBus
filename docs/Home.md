# OpenServiceBus

A zero-dependency, embeddable **Azure Service Bus emulator** for .NET. Speaks real AMQP 1.0
over TCP and WebSocket so any Service Bus client connects unmodified.

This wiki mirrors [`docs/`](https://github.com/mauritsarissen/OpenServiceBus/tree/main/docs)
in the repo - every page here is committed in source.

## Pages

### Getting started

- **[Getting Started](Getting-Started)** - install + first send/receive in three flavours.
- **[Configuration](Configuration)** - `config.json` schema, env vars, every option.
- **[Architecture](Architecture)** - assemblies and how they wire together.

### Running the broker

- **[Docker](Docker)** - image, compose, ports, persistence, healthcheck.
- **[Persistence](Persistence)** - SQLite store, schema, restart semantics.
- **[Explorer UI](Explorer)** - browser-based console for testing.

### Service Bus features

- **[SDK Admin Client](Admin-Client)** - `ServiceBusAdministrationClient` entity CRUD on the AMQP port.
- **[Entity Status](Entity-Status)** - freeze or drain entities with `Disabled`/`SendDisabled`/`ReceiveDisabled`.
- **[Auto-Delete On Idle](Auto-Delete-On-Idle)** - entities that clean themselves up after inactivity.
- **[Size Limits](Size-Limits)** - message size ceilings and entity quotas with standard-tier defaults.
- **[Topics and Subscriptions](Topics-and-Subscriptions)** - fan-out with SQL + correlation filters.
- **[Sessions](Sessions)** - per-session FIFO, session state, session locks.
- **[Auto-Forwarding](Auto-Forwarding)** - `ForwardTo`, DLQ forwarding, cycle protection.
- **[Transactions](Transactions)** - `TransactionScope`, atomic batches, dispositions.
- **[Duplicate Detection](Duplicate-Detection)** - `RequiresDuplicateDetection`, sliding window.

### Wire protocol

- **[WebSocket Transport](WebSocket-Transport)** - AMQP-over-WebSocket bridge.

### Operations

- **[OpenTelemetry](OpenTelemetry)** - tracing + metrics emitted natively.
- **[Testing](Testing)** - `OpenServiceBusTestHost`, fake time, parity tests.

### Contributing

- **[Contributing](Contributing)** - repo layout, conventions, release process.
- **[Roadmap](ROADMAP)** - what's next.

## At a glance

|                          | OpenServiceBus                 | Official Microsoft emulator |
| ------------------------ | ------------------------------ | --------------------------- |
| License                  | MIT                            | EULA-gated                  |
| Embeddable in tests      | ✅                             | ❌                          |
| Container size           | ~300 MB Alpine                 | ~2 GB                       |
| Persistence              | In-memory or SQLite            | SQL Server (required)       |
| Transports               | AMQP-TCP + AMQP-over-WebSocket | AMQP-TCP only               |
| Telemetry                | Native OpenTelemetry           | None                        |
| `config.json` compatible | ✅                             | ✅                          |
| `ServiceBusAdministrationClient` | ✅ (ATOM API, same port) | ❌                          |

## Install

```bash
# Tests / in-process
dotnet add package OpenServiceBus.Testing

# Docker
docker run -d -p 5672:5672 -p 5300:5300 -v osb-data:/data mauritsarissen/openservicebus:latest

# Source
git clone https://github.com/mauritsarissen/OpenServiceBus && cd OpenServiceBus
dotnet run --project src/OpenServiceBus.Host
```

Then point any Service Bus client at:

```text
Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true
```
