# Cross-SDK smoke tests

The four official Azure Service Bus client stacks speak measurably different AMQP:

| SDK      | AMQP library         | Notable quirks                                                                    |
| -------- | -------------------- | --------------------------------------------------------------------------------- |
| .NET     | Microsoft.Azure.Amqp | Sets reply-link target addresses, string message-ids; also covered by `dotnet test` |
| Node.js  | rhea                 | Attaches reply links with an **empty target address**, reply-to == link name (issue #1) |
| Java     | proton-j             | **ulong message-ids**, fixed `cbs-client-reply-to` reply address (issue #1)       |
| Python   | pyamqp               | **Hard-indexes optional trailing frame fields** (open[9], begin[7], attach[13]), full-URI entity addresses |

Passing one SDK proves little about the others - issue #1 (Node and Java SDKs completely
unable to connect) shipped through a fully green .NET suite, and the first Python run
found two more parity gaps within minutes. These smokes drive a real
running Host with the real SDKs, run in CI on every push and PR (`ci.yml`), and gate
every release (`release.yml`) via the shared `sdk-smoke.yml` workflow.

## The canonical sequence

Every smoke performs **exactly the same operations in the same order** against the same
entities (declared once in [`config.json`](config.json)):

```
send            -> $cbs auth + queue send        (smoke-queue)
peek            -> $management request/response
receive         -> peek-lock delivery
complete        -> disposition
schedule        -> $management scheduled enqueue
cancelSchedule  -> $management cancel
session receive -> session accept + FIFO receive (smoke-sessions)
```

Output format is identical too: one `PASS <op>` / `FAIL <op>` line per step and a final
`<LANG> SMOKE: ALL PASS|FAILED`, exit code 0/1 - so the four logs are diffable
line-for-line.

## Running locally

```bash
# 1. Start a broker with the smoke entities (from the repo root):
OPENSERVICEBUS_CONFIG=$PWD/tests/sdk-smoke/config.json \
  dotnet run --project src/OpenServiceBus.Host --no-launch-profile

# 2. .NET:
dotnet run --project tests/sdk-smoke/dotnet/DotnetSmoke.csproj

# 3. Node (requires Node 20+):
cd tests/sdk-smoke/node && npm ci && npm run smoke

# 4. Java (requires JDK 17+ and Maven):
cd tests/sdk-smoke/java && mvn -q compile exec:java

# 5. Python (requires Python 3.10+):
cd tests/sdk-smoke/python && pip install -r requirements.txt && python smoke.py
```

All scripts default to `localhost:5672` and honor a `SMOKE_CONNECTION` environment
variable. Note: the Java SDK ignores a custom port in emulator connection strings and
always dials 5672.
