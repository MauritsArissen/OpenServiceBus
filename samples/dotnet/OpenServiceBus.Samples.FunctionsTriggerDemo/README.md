# OpenServiceBus.Samples.FunctionsTriggerDemo

Interactive Azure Functions app that exercises the full Service Bus trigger surface
against a running OpenServiceBus broker. Start it with `func start` and drive it from
the Explorer or `curl` - every function is annotated with what it shows off.

## Functions in this app

| Function                         | Trigger                                                         | Demonstrates                                                                                                                                                      |
| -------------------------------- | --------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OrderProcessor.OnOrder`         | `ServiceBusTrigger("orders")`                                   | Plain peek-lock with auto-complete on success and auto-abandon on throw. Send body `fail-...` to force a retry-to-DLQ loop; send `slow` to exercise lock renewal. |
| `BatchProcessor.OnBatch`         | `ServiceBusTrigger("batch-queue", IsBatched=true)`              | Batched receive (`IList<ServiceBusReceivedMessage>` in one invocation).                                                                                           |
| `ManualDisposition.OnManual`     | `ServiceBusTrigger("manual-queue", AutoCompleteMessages=false)` | Manual complete/abandon/dead-letter from inside the function, driven by message body.                                                                             |
| `DeadLetterWatcher.OnDeadLetter` | `ServiceBusTrigger("orders/$DeadLetterQueue")`                  | Trigger on the DLQ as a first-class sub-entity.                                                                                                                   |
| `HttpEnqueue.Enqueue*`           | `HttpTrigger` + `[ServiceBusOutput]`                            | HTTP-driven send via Functions output bindings - proves OpenServiceBus handles the sending side of the binding pipeline too.                                      |

## Run it

The sample expects a broker reachable at `sb://localhost:5672` with three queues
declared: `orders`, `manual-queue`, `batch-queue`. The bundled compose file sets all
of that up:

```bash
# Terminal 1: broker (with the right queues pre-declared)
docker compose up

# Terminal 2: Explorer UI (optional - easier to drive than curl)
dotnet run --project ../../src/OpenServiceBus.Explorer

# Terminal 3: Functions worker
func start
```

## Drive it

Use the **Explorer** at <http://localhost:5400> to send messages to each queue, or use
the HTTP routes:

```bash
# Enqueue a normal order
curl -X POST localhost:7071/api/orders -d 'sale-123'

# Force a retry-to-DLQ
curl -X POST localhost:7071/api/orders -d 'fail-this'

# Enqueue with manual disposition (body decides outcome)
curl -X POST localhost:7071/api/manual -d 'complete'
curl -X POST localhost:7071/api/manual -d 'abandon'
curl -X POST localhost:7071/api/manual -d 'deadletter'

# Enqueue a batch
curl -X POST localhost:7071/api/batch -d 'a,b,c,d,e'
```

Watch the Functions worker logs to see how each path unfolds. The DLQ watcher fires
automatically when retries exhaust on `orders`.

## Files

| File                  | Purpose                                                          |
| --------------------- | ---------------------------------------------------------------- |
| `Program.cs`          | Isolated-worker `HostBuilder` entry point                        |
| `Functions/*.cs`      | The five trigger functions described above                       |
| `host.json`           | Service Bus extension config (max-concurrent, batch sizes, etc.) |
| `local.settings.json` | `ServiceBusConnection` pointing at `sb://localhost:5672`         |
| `config.json`         | OpenServiceBus declarative bootstrap                             |
| `docker-compose.yml`  | Broker with `config.json` mounted in                             |
