# Transactions

OpenServiceBus implements the AMQP **transaction coordinator** plus transactional sends and
dispositions, so client code using `System.Transactions.TransactionScope` works unmodified.

```csharp
using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
{
    await sender.SendMessageAsync(new ServiceBusMessage("a") { MessageId = "1" });
    await sender.SendMessageAsync(new ServiceBusMessage("b") { MessageId = "2" });
    await receiver.CompleteMessageAsync(existingMessage);
    scope.Complete();   // commit; without this everything rolls back
}
```

On commit, all three operations apply atomically. On scope-dispose-without-`Complete`,
nothing happens - the buffered sends are discarded and the buffered complete is rolled
back so the message stays under lock until it expires.

## What's covered

| Operation in scope            | Behavior                                                           |
| ----------------------------- | ------------------------------------------------------------------ |
| `SendMessageAsync`            | Buffered, applied on commit                                        |
| `SendMessagesAsync` (batched) | Buffered, applied as a batch on commit                             |
| `CompleteMessageAsync`        | Buffered, applied on commit; the message stays locked until commit |
| `AbandonMessageAsync`         | Buffered, applied on commit; the lock isn't released until commit  |
| `DeadLetterMessageAsync`      | Buffered, applied on commit                                        |
| `DeferMessageAsync`           | Buffered, applied on commit                                        |

Cross-entity operations within a single scope work: send to queue A, complete from queue
B, send to topic C - all-or-nothing.

## How it works

1. The first transactional operation in a scope opens a **coordinator link** with
   `Target = Coordinator`. The broker accepts the attach via a special address resolver.
2. The client sends a `Declare` message; the broker allocates an opaque 8-byte txn-id and
   responds with a `Declared` outcome carrying the id.
3. Subsequent sends and dispositions arrive with their `DeliveryState` wrapped in a
   `TransactionalState { TxnId, Outcome }`. The broker:
   - For sends: enlists the enqueue under the txn instead of running it now, then replies
     with `TransactionalState { TxnId, Outcome = Accepted }`.
   - For dispositions: enlists the store op (complete/abandon/etc) under the txn, replies
     transactional-Accepted.
4. On `scope.Complete()` the SDK sends a `Discharge { TxnId, Fail = false }`. The broker
   replays every enlisted operation in order under the txn lock, then disposes the
   discharge with `Accepted`.
5. On dispose-without-complete the SDK sends `Discharge { TxnId, Fail = true }`. The
   broker discards the buffered ops and disposes the discharge with `Accepted`.

## Limitations

- **Distributed transactions are not supported.** The broker is single-node by design -
  the coordinator promotes nothing; `TransactionPromotionException` is reported back to
  the SDK if a promotion attempt happens. (DTC-style two-phase commit was explicitly
  scoped out of the roadmap.)
- **Commit is acknowledged before the replay finishes.** The discharge is currently
  settled while the buffered operations replay in the background, so an immediate
  receive after `scope.Complete()` can race the enqueue, and a replay failure is logged
  rather than reported to the client. Tracked as a known bug.
- **Commit replay is best-effort, not atomic.** Operations replay in order, but a
  failing op is logged and skipped rather than rolling back the ops already applied.
  There is no rollback on crash either: if the broker dies mid-replay,
  partially-applied operations stay applied (in both stores).
- **Batched sends to topics bypass the transaction.** `SendMessagesAsync` (batch) to a
  queue enlists correctly; the same batch to a topic currently fans out immediately and
  ignores rollback. Single sends to topics enlist correctly. Tracked as a known bug.
- **Per-txn isolation is not enforced.** A read inside a transactional receive may see
  data written by another concurrent client (no read-your-writes guarantee). This is also
  Service Bus's behavior - transactions cover writes, not reads.

## Tests

- [`tests/OpenServiceBus.IntegrationTests/TransactionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/TransactionTests.cs)
  - 5 SDK-driven scenarios: commit visibility, rollback drops sends, multi-send atomicity,
    rollback of buffered `Complete` keeps the message locked, committed `Complete` removes it.

## See also

- [Architecture](Architecture) - where the coordinator processor and `TransactionManager` sit.
