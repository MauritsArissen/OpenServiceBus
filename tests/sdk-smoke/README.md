# Cross-SDK smoke tests

The three official Azure Service Bus client stacks speak measurably different AMQP:

| SDK      | AMQP library | Notable quirks                                                                 |
| -------- | ------------ | ------------------------------------------------------------------------------ |
| .NET     | Microsoft.Azure.Amqp | Sets reply-link target addresses; covered by the main `dotnet test` suite |
| Node.js  | rhea         | Attaches reply links with an **empty target address**, reply-to == link name (GitHub issue #1) |
| Java     | proton-j     | Its own CBS/management framing                                                 |

Passing the .NET suite alone proves very little about the other two - issue #1 (Node SDK
completely unable to connect) shipped through a fully green .NET suite. These smoke tests
drive a real running Host with the real Node and Java SDKs and run in CI on every push
and PR, so every release is verified against all three stacks.

## What each smoke covers

Send (CBS auth - the issue #1 path), peek, schedule + cancel (the `$management`
request/response path), receive + complete, and session accept + receive.

## Running locally

```bash
# 1. Start a broker with the smoke entities (from the repo root):
OPENSERVICEBUS_CONFIG=$PWD/tests/sdk-smoke/config.json \
  dotnet run --project src/OpenServiceBus.Host --no-launch-profile

# 2. Node (requires Node 20+):
cd tests/sdk-smoke/node && npm ci && npm run smoke

# 3. Java (requires JDK 17+ and Maven):
cd tests/sdk-smoke/java && mvn -q compile exec:java
```

Both scripts default to `localhost:5672` and honor a `SMOKE_CONNECTION` environment
variable if the broker runs elsewhere. Exit code 0 = all pass.
