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

## Atomic commit

Commit is two-phase. Every buffered operation carries a side-effect-free **validation**
alongside the deferred **apply**:

1. **Validate everything.** Dispositions check their peek-lock is still held (lock expired
   or taken over means the op can no longer apply); sends and topic publishes check the
   target entity still exists. A validation failure rejects the discharge with
   `amqp:transaction:rollback` - the client's commit throws - and **nothing** has been
   applied: no partial sends, no lost dispositions.
2. **Apply everything.** Only after every operation validated do the store mutations run,
   in enlist order.

Everything the supported operation set can fail on is covered by the validation phase, so
an apply-phase failure requires a precondition to be lost in the instants between the two
phases (for example a peek-lock expiring mid-commit on a wall-clock boundary). In that
residual window the replay stops at the faulting operation and the discharge is rejected,
but operations applied before it stay applied - the in-memory and SQLite stores have no
undo log. The window is a few microseconds per commit; it cannot be hit by anything a
client does, only by a precondition expiring between the validation pass and the apply
pass of the same commit.

A rejected discharge rolls the transaction back: the buffered operations are discarded and
the txn-id is forgotten, matching Azure.

## Cross-entity transactions

`ServiceBusClientOptions.EnableCrossEntityTransactions` is supported. The SDK implements
it by opening every link - with the transaction controller attached FIRST, as the signal -
on one shared AMQP session. The broker mirrors Azure's semantics:

- A session carrying a coordinator link is a **transaction group**. The first entity link
  attached on that session pins the group's **via entity** (receive from A first, and A is
  the via entity; send to B first, and B is).
- **Sender** attaches to any entity on the group session are allowed - operations on them
  group under the same transaction, so a receive from A and a send to B commit or roll
  back together.
- **Receiver** attaches on a different entity than the via entity are refused with
  `amqp:not-allowed`, which the SDK surfaces as `InvalidOperationException` - the same
  exception Azure produces for the single-via-entity restriction.
- The Java SDK's cross-entity shape (and the legacy track-1 send-via) attaches senders
  whose link TARGET is the via entity and whose
  `com.microsoft:transfer-destination-address` link property names the real destination.
  The broker recognizes the property, validates both entities exist, and wires the sender
  to the destination.

One deliberate simplification: real Service Bus physically routes send-via messages
through the via entity's transfer queue before forwarding them to the destination. The
emulator skips that intermediate hop and enqueues directly on the destination - the
grouping, atomicity and single-via-entity restriction are preserved, but transfer-queue
internals (for example via-entity annotations on in-flight transfers) are not observable.

Python (`azure-servicebus`) and JavaScript (`@azure/service-bus`) do not expose AMQP
transactions, so cross-entity transactions are a .NET + Java surface.

## Limitations

- **Distributed transactions are not supported.** The broker is single-node by design -
  the coordinator promotes nothing; `TransactionPromotionException` is reported back to
  the SDK if a promotion attempt happens. (DTC-style two-phase commit was explicitly
  scoped out of the roadmap.)
- **No crash atomicity.** The two-phase commit guarantees no partial application for
  anything a client can cause, but if the broker process dies mid-apply, operations
  already applied stay applied (in both stores). There is also the microseconds-wide
  mid-commit window described under [Atomic commit](#atomic-commit).
- **The physical send-via transfer hop is skipped.** See
  [Cross-entity transactions](#cross-entity-transactions).
- **Per-txn isolation is not enforced.** A read inside a transactional receive may see
  data written by another concurrent client (no read-your-writes guarantee). This is also
  Service Bus's behavior - transactions cover writes, not reads.

## Tests

- [`tests/OpenServiceBus.IntegrationTests/TransactionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/TransactionTests.cs)
  - 5 SDK-driven scenarios: commit visibility, rollback drops sends, multi-send atomicity,
    rollback of buffered `Complete` keeps the message locked, committed `Complete` removes it.
- [`tests/OpenServiceBus.IntegrationTests/CrossEntityTransactionTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/CrossEntityTransactionTests.cs)
  - `EnableCrossEntityTransactions` scenarios: receive-from-A + send-to-B commit and
    rollback, the second-receive-entity `InvalidOperationException`, and a stale-lock
    commit rejected with nothing applied.
- [`tests/OpenServiceBus.InMemoryStorage.Tests/TransactionManagerTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.InMemoryStorage.Tests/TransactionManagerTests.cs)
  - the two-phase commit itself: validation ordering, atomic rejection, apply faults.
- [`tests/OpenServiceBus.Amqp.Tests/TransactionGroupsTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.Amqp.Tests/TransactionGroupsTests.cs)
  - via-entity pinning and the receiver restriction.
- [`tests/OpenServiceBus.Amqp.WireTests/SendViaWireTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.Amqp.WireTests/SendViaWireTests.cs)
  - the `com.microsoft:transfer-destination-address` attach shape on the wire.

## See also

- [Architecture](Architecture) - where the coordinator processor and `TransactionManager` sit.
