# OpenServiceBus.Samples.Sessions

Demonstrates **session-enabled queues**: per-session FIFO ordering with exclusive session
ownership. Two workers each claim one tenant's session and drain it in order - while
the wire-order of incoming messages was interleaved.

## What it demonstrates

- `RequiresSession=true` queue declared in `config.json`.
- Sender side: every message carries a `SessionId` (otherwise the broker rejects it).
- Receiver side: `AcceptNextSessionAsync` - workers grab whichever session has unclaimed
  messages. Two workers, two tenants, perfect parallelism.
- Per-session FIFO: each worker sees its tenant's messages 1 → 2 → 3 in order, even
  though the broker received them interleaved.

## Run it

```bash
docker compose up -d
dotnet run
docker compose down -v
```

Expected output (worker order may vary; FIFO within a session is guaranteed):

```
Sending interleaved messages to 'sessioned'…

Starting two workers - each will grab one available session…
[worker-1] locked session 'tenant-A'
[worker-2] locked session 'tenant-B'
[worker-1] A-1 → "tenant-A msg 1"
[worker-2] B-1 → "tenant-B msg 1"
[worker-1] A-2 → "tenant-A msg 2"
[worker-2] B-2 → "tenant-B msg 2"
[worker-1] A-3 → "tenant-A msg 3"
[worker-2] B-3 → "tenant-B msg 3"
[worker-1] no more messages - releasing session.
[worker-2] no more messages - releasing session.
```

## Files

| File                 | Purpose                                                |
| -------------------- | ------------------------------------------------------ |
| `Program.cs`         | Interleaved sends, two parallel session-locked workers |
| `config.json`        | Declares `sessioned` with `RequiresSession=true`       |
| `docker-compose.yml` | Broker with `config.json` mounted in                   |

## See also

- [Sessions](../../docs/Sessions.md) - `AcceptSessionAsync` vs `AcceptNextSessionAsync`,
  session state, lock renewal, wire protocol details.
