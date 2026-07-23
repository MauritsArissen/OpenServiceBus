# Configuration

OpenServiceBus.Host reads configuration from three layers, in order of precedence:

1. **CLI args** (e.g. `--config /path/to/config.json`)
2. **Environment variables** - double-underscore-as-separator, ASP.NET Core convention
3. **`appsettings.json`** / `appsettings.{Environment}.json`

## Environment variables

| Key                                        | Default                   | Purpose                                           |
| ------------------------------------------ | ------------------------- | ------------------------------------------------- |
| `OpenServiceBus__Amqp__Host`               | `0.0.0.0`                 | Host the AMQP listener binds to                   |
| `OpenServiceBus__Amqp__Port`               | `5672`                    | AMQP listener port                                |
| `OpenServiceBus__Amqp__ContainerId`        | random                    | Container-id reported in AMQP Open frame          |
| `OpenServiceBus__Amqp__IdleTimeoutMs`      | `30000`                   | AMQP idle-timeout advertised to clients           |
| `OpenServiceBus__Amqp__MaxMessageSize`     | `262144`                  | Max message size on link attach (256 KB)          |
| `OpenServiceBus__Amqp__RequireSasAuth`     | `false`                   | Enforce `$cbs put-token` validation               |
| `OpenServiceBus__Amqp__SasKeys`            | -                         | `name=key` map for SAS-auth (when enabled)        |
| `OpenServiceBus__Amqp__EnableFrameTracing` | `false`                   | Log every AMQP frame at Debug                     |
| `OpenServiceBus__Storage__Mode`            | `InMemory`                | `InMemory` or `Sqlite`                            |
| `OpenServiceBus__Storage__DataSource`      | `:memory:`                | SQLite path (use `/data/broker.db` in containers) |
| `OpenServiceBus__WebSockets__Enabled`      | `false`                   | Start the AMQP-over-WebSocket bridge              |
| `OpenServiceBus__WebSockets__Host`         | `+`                       | HttpListener host prefix                          |
| `OpenServiceBus__WebSockets__Port`         | `5673`                    | WebSocket port                                    |
| `OpenServiceBus__WebSockets__Path`         | `/$servicebus/websocket/` | URL path                                          |
| `OpenServiceBus__WebSockets__UpstreamHost` | `127.0.0.1`               | Loopback host to tunnel to                        |
| `OpenServiceBus__WebSockets__UpstreamPort` | (AMQP port)               | Loopback port to tunnel to                        |
| `OPENSERVICEBUS_CONFIG`                    | -                         | Path to a Microsoft-emulator `config.json`        |
| `ASPNETCORE_URLS`                          | `http://+:5300`           | Kestrel binding for the management API            |

## `config.json` - declarative queue/topic bootstrap

OpenServiceBus reads the same `config.json` format as the official Microsoft Service Bus
emulator. File resolution order:

1. `--config <path>` CLI argument
2. `OPENSERVICEBUS_CONFIG` environment variable
3. `config.json` in the host's content-root directory

If none of these resolves to an existing file, the host starts with no pre-declared queues
and logs an informational message. Parse errors are logged but do **not** stop the host.

### Schema

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
              "MaxDeliveryCount": 10,
              "DefaultMessageTimeToLive": "PT1H",
              "DeadLetteringOnMessageExpiration": false,
              "RequiresSession": false,
              "RequiresDuplicateDetection": false,
              "DuplicateDetectionHistoryTimeWindow": "PT10M",
              "ForwardTo": "",
              "ForwardDeadLetteredMessagesTo": ""
            }
          }
        ]
      }
    ]
  }
}
```

### Field reference

| Field                                 | Type              | Default     | Notes                                                                         |
| ------------------------------------- | ----------------- | ----------- | ----------------------------------------------------------------------------- |
| `LockDuration`                        | ISO-8601 duration | `PT1M`      | Peek-lock TTL                                                                 |
| `MaxDeliveryCount`                    | int               | `10`        | Deliveries before auto-DLQ                                                    |
| `DefaultMessageTimeToLive`            | ISO-8601 duration | (unlimited) | Per-queue TTL (per-message TTL still wins when shorter)                       |
| `DeadLetteringOnMessageExpiration`    | bool              | `false`     | Auto-DLQ expired messages instead of dropping them                            |
| `RequiresSession`                     | bool              | `false`     | Enable session semantics - see [Sessions](Sessions)                           |
| `RequiresDuplicateDetection`          | bool              | `false`     | Enable dedup - see [Duplicate Detection](Duplicate-Detection)                 |
| `DuplicateDetectionHistoryTimeWindow` | ISO-8601 duration | `PT10M`     | Sliding dedup window                                                          |
| `ForwardTo`                           | string            | -           | Auto-forward target (queue or topic) - see [Auto-Forwarding](Auto-Forwarding) |
| `ForwardDeadLetteredMessagesTo`       | string            | -           | DLQ-forward target                                                            |

Topics + subscriptions in `config.json` are accepted in the same shape as the Microsoft
emulator (`"Topics": [ { "Name": ..., "Subscriptions": [ ... ] } ]`); the
[`OpenServiceBus.Samples.TopicsAndFilters`](https://github.com/mauritsarissen/OpenServiceBus/tree/main/samples/dotnet/OpenServiceBus.Samples.TopicsAndFilters)
sample ships a `config.json` covering topics, subscriptions, and rule shapes (SQL + correlation).

## SAS authentication

Default broker mode is "emulator" - clients authenticate via SASL ANONYMOUS and `$cbs`
accepts any token. To enforce SAS:

```json
{
  "OpenServiceBus": {
    "Amqp": {
      "RequireSasAuth": true,
      "SasKeys": {
        "RootManageSharedAccessKey": "SAS_KEY_VALUE",
        "ListenOnly": "other-key"
      }
    }
  }
}
```

When `RequireSasAuth=true` is set, the broker rejects every connection that doesn't include
a valid `put-token` over `$cbs` before the first `attach`. Set the SDK connection string's
`SharedAccessKeyName=`/`SharedAccessKey=` to one of the configured keys.

## Storage mode

```json
{
  "OpenServiceBus": {
    "Storage": {
      "Mode": "Sqlite",
      "DataSource": "/data/broker.db"
    }
  }
}
```

- `InMemory` (default) - fastest, no persistence, ideal for tests.
- `Sqlite` - single `.db` file, WAL mode, persists across restarts. See [Persistence](Persistence).
  - `DataSource` accepts a filesystem path or `:memory:`.
