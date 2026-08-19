# Docker

The OpenServiceBus container image is multi-stage Alpine, runs as a non-root user, and
ships the **broker AND the Explorer UI** in one image. SQLite-backed by default so
messages survive container recreates when you mount a volume.

## Pull

```bash
docker pull mauritsarissen/openservicebus:latest
# or, equivalently, from GHCR
docker pull ghcr.io/mauritsarissen/openservicebus:latest
```

Both registries are kept in sync by the release pipeline. Docker Hub is the recommended
default.

## Ports

| Port   | Protocol       | Service         | When                                                       |
| ------ | -------------- | --------------- | ---------------------------------------------------------- |
| `5672` | AMQP + HTTP    | Broker          | Always exposed - data plane (Service Bus SDK, AMQPNetLite) AND the ATOM management API (`ServiceBusAdministrationClient`); a protocol front door serves both on this one port, see [Admin-Client](Admin-Client.md) |
| `5300` | HTTP           | Management API  | Always exposed - JSON REST CRUD + `/health`                |
| `5400` | HTTP           | **Explorer UI** | Always exposed - open <http://localhost:5400> in a browser |
| `5673` | HTTP/WebSocket | AMQP-over-WS    | Only when `OPENSERVICEBUS__WEBSOCKETS__ENABLED=true`       |

You must publish `5400` (or remap it to another host port - `-p 8080:5400` works) for the
Explorer to be reachable. The internal port is always `5400`; the host port is up to you.

## One-shot run

```bash
docker run -d --name openservicebus \
  -p 5672:5672 -p 5300:5300 -p 5400:5400 \
  -v osb-data:/data \
  mauritsarissen/openservicebus:latest
```

Then:

- Point the Azure SDK at:
  ```text
  Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true
  ```
- Open the Explorer at <http://localhost:5400>.
- Hit `/health` at <http://localhost:5300/health>.

## Reference `docker-compose.yml`

The richer setup most projects end up wanting: persistent volume, mounted `config.json`
that declares queues + topics + subscriptions + forwarding, broker + Explorer both
reachable.

```yaml
# docker-compose.yml
services:
  openservicebus:
    image: mauritsarissen/openservicebus:latest
    container_name: openservicebus
    ports:
      - "5672:5672" # AMQP + ATOM management - both Service Bus SDK clients connect here
      - "5300:5300" # REST management API + /health
      - "5400:5400" # Explorer UI - open http://localhost:5400
    environment:
      OPENSERVICEBUS__STORAGE__MODE: Sqlite
      OPENSERVICEBUS__STORAGE__DATASOURCE: /data/broker.db
      OPENSERVICEBUS_CONFIG: /etc/openservicebus/config.json
    volumes:
      - osb-data:/data
      - ./config.json:/etc/openservicebus/config.json:ro
    restart: unless-stopped

volumes:
  osb-data:
```

`docker compose up -d` and you've got the broker + Explorer + persistent storage in one
shot.

## Reference `config.json`

The mounted file the compose example points at. Declares the common Service Bus shapes:
plain queues, session-enabled queues, queues with duplicate detection, topics with
subscriptions and SQL/correlation filter rules, and auto-forwarding chains.

> **Note about DLQs.** You don't declare dead-letter queues here - every queue and every
> subscription **automatically** gets a `<entity>/$DeadLetterQueue` sibling created by the
> broker. Just receive from that address when you need to drain it.

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
              "MaxDeliveryCount": 5,
              "DefaultMessageTimeToLive": "PT1H",
              "DeadLetteringOnMessageExpiration": true
            }
          },
          {
            "Name": "tenant-events",
            "Properties": {
              "RequiresSession": true,
              "LockDuration": "PT1M",
              "MaxDeliveryCount": 10
            }
          },
          {
            "Name": "deduped-ingress",
            "Properties": {
              "RequiresDuplicateDetection": true,
              "DuplicateDetectionHistoryTimeWindow": "PT10M"
            }
          },
          {
            "Name": "audit-archive",
            "Properties": {
              "MaxDeliveryCount": 20,
              "DefaultMessageTimeToLive": "P7D"
            }
          },
          {
            "Name": "ingress-gateway",
            "Properties": {
              "ForwardTo": "audit-archive"
            }
          }
        ],
        "Topics": [
          {
            "Name": "events",
            "Properties": {
              "DefaultMessageTimeToLive": "PT1H"
            },
            "Subscriptions": [
              {
                "Name": "all",
                "Properties": { "MaxDeliveryCount": 10 }
              },
              {
                "Name": "eu-orders",
                "Properties": { "MaxDeliveryCount": 5 },
                "Rules": [
                  {
                    "Name": "EuHighPriority",
                    "Properties": {
                      "FilterType": "Sql",
                      "SqlFilter": {
                        "SqlExpression": "region = 'eu' AND priority >= 5"
                      }
                    }
                  }
                ]
              },
              {
                "Name": "high-priority",
                "Properties": { "MaxDeliveryCount": 5 },
                "Rules": [
                  {
                    "Name": "PriorityNine",
                    "Properties": {
                      "FilterType": "Correlation",
                      "CorrelationFilter": {
                        "Properties": { "priority": "9" }
                      }
                    }
                  }
                ]
              },
              {
                "Name": "archive-bridge",
                "Properties": {
                  "MaxDeliveryCount": 5,
                  "ForwardTo": "audit-archive"
                }
              }
            ]
          }
        ]
      }
    ]
  }
}
```

What this gives you on startup:

- **5 queues** - `orders`, `tenant-events` (session-enabled), `deduped-ingress` (dedup
  on), `audit-archive`, `ingress-gateway` (every send to it auto-forwards to
  `audit-archive`).
- **1 topic** `events` with **4 subscriptions** - `all` (`$Default` `TrueFilter` -
  receives everything), `eu-orders` (SQL filter), `high-priority` (correlation filter),
  `archive-bridge` (forwards everything matching `$Default` to `audit-archive`).
- **9 implicit DLQs** - one per queue, one per subscription - addressable as
  `<entity>/$DeadLetterQueue`. No declaration needed.

Open the Explorer at <http://localhost:5400> to see the full topology, send test
messages, and watch them route through filters and forwarding chains in real time.

## Environment variables

| Variable                               | Default in image  | Notes                                                                     |
| -------------------------------------- | ----------------- | ------------------------------------------------------------------------- |
| `OPENSERVICEBUS__STORAGE__MODE`        | `Sqlite`          | Override to `InMemory` for ephemeral mode                                 |
| `OPENSERVICEBUS__STORAGE__DATASOURCE`  | `/data/broker.db` | Where the SQLite file lives - must be on a mounted volume for persistence |
| `OPENSERVICEBUS__AMQP__PORT`           | `5672`            | AMQP + ATOM management port                                               |
| `OPENSERVICEBUS__AMQP__REQUIRESASAUTH` | `false`           | Flip to `true` and provide `__SASKEYS__<name>` to enforce SAS             |
| `OPENSERVICEBUS__WEBSOCKETS__ENABLED`  | `false`           | Start the AMQP-over-WebSocket bridge                                      |
| `OPENSERVICEBUS__WEBSOCKETS__PORT`     | `5673`            | WebSocket port                                                            |
| `OPENSERVICEBUS_CONFIG`                | -                 | Path to a mounted `config.json` for declarative bootstrap                 |
| `ASPNETCORE_URLS_HOST`                 | `http://+:5300`   | Bind URL for the broker's management Kestrel                              |
| `ASPNETCORE_URLS_EXPLORER`             | `http://+:5400`   | Bind URL for the Explorer's Kestrel                                       |

Full reference: [Configuration](Configuration).

## Persistence

The container persists messages **only if** you mount `/data`. Without a volume the DB
file is inside the container layer and disappears when the container is removed. With a
named volume (`-v osb-data:/data` or compose `volumes:`), the same `broker.db` is reused
across recreates - proven by an end-to-end test: send a message via the SDK, `docker stop
&& docker rm`, `docker run` from the same volume, the SDK receives the same message.

> 💡 The whole topology survives the restart with it: queue and topic settings,
> subscription settings (`RequiresSession`, `ForwardTo`, `AutoDeleteOnIdle`, …) and every
> subscription rule, filter parameters and actions included. Declaring topology in
> `config.json` is still the way to pin a known-good shape - the bootstrap service runs
> before rehydration and wins where the two disagree.

See [Persistence](Persistence) for the SQLite schema and restart semantics.

## Healthcheck

The image declares a healthcheck against `/health` (the broker's, on port 5300):

```dockerfile
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:5300/health || exit 1
```

`docker ps` reports `healthy` / `unhealthy` based on whether the broker can serve HTTP -
useful for compose health gates and Kubernetes readiness probes.

## How the container runs two apps

The image bakes both `OpenServiceBus.Host` (the broker) and `OpenServiceBus.Explorer` (the
UI) and starts them under a small POSIX-safe entrypoint script. Both processes share the
same container lifecycle: SIGTERM from `docker stop` flows to both, and if either one
crashes the container exits non-zero so your orchestrator can restart it.

The Explorer talks to the broker over loopback (`127.0.0.1:5300` for the management API,
`127.0.0.1:5672` for AMQP), so they're tightly coupled by design - there's no benefit to
running them in separate containers.

## Building locally

The repo's [`Dockerfile`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/Dockerfile)
is the same one the release pipeline uses:

```bash
docker build -t openservicebus:dev .
docker run --rm -p 5672:5672 -p 5300:5300 -p 5400:5400 openservicebus:dev
```

The build is layer-cache friendly - `.csproj` files copied first for restore, source
second for compile. Cold build: ~30s. Warm rebuild (source-only edit): a few seconds.
