# OpenServiceBus.Samples.Functions

The minimal Azure Functions sample - a single isolated-worker function with one
`ServiceBusTrigger` pointed at the queue `integration-queue`. This is also the project the
The Functions integration test spins up via `func` to prove OpenServiceBus is a drop-in
replacement for Azure Service Bus.

What it demonstrates:

- `Microsoft.Azure.Functions.Worker` isolated-worker model targeting `.NET 8`.
- Peek-lock receive via `ServiceBusTrigger`, with `autoCompleteMessages` on (default).
- Writes the message id of every processed message to a sentinel file so an external
  harness can assert delivery.

## Run it

The sample expects a broker reachable at `sb://localhost:5672`. Easiest way to bring one
up is the bundled compose file:

```bash
# Terminal 1: broker
docker compose up

# Terminal 2: Functions worker
cd samples/OpenServiceBus.Samples.Functions
func start
```

Then send a test message via the Explorer at <http://localhost:5400> (point it at queue
`integration-queue`) or from any other Service Bus client.

## Files

| File                  | Purpose                                                                 |
| --------------------- | ----------------------------------------------------------------------- |
| `Program.cs`          | Standard isolated-worker `HostBuilder` entry point                      |
| `OrderProcessor.cs`   | The trigger function - receives + logs + writes to sentinel             |
| `host.json`           | Functions runtime config (default Service Bus extension settings)       |
| `local.settings.json` | `ServiceBusConnection` pointing at `sb://localhost:5672`                |
| `config.json`         | OpenServiceBus declarative bootstrap - pre-declares `integration-queue` |
| `docker-compose.yml`  | Brings up the broker with this `config.json` mounted in                 |

## Notes

- `local.settings.json` ships with `UseDevelopmentEmulator=true` so the SDK accepts a
  plain-TCP broker. Don't ship this file in a production deploy.
- The sentinel-file behavior is gated on the `OSB_FUNCTIONS_SENTINEL_FILE` env var. When
  unset, the function logs but doesn't write.
