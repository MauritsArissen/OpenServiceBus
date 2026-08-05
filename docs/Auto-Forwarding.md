# Auto-Forwarding

Two pointer fields on every queue + subscription let the broker transparently re-route
messages to another entity - same wire-level semantics as Azure Service Bus.

| Field                           | Purpose                                                                            |
| ------------------------------- | ---------------------------------------------------------------------------------- |
| `ForwardTo`                     | Destination for every accepted live message                                        |
| `ForwardDeadLetteredMessagesTo` | Destination for messages that would otherwise land in the local `$DeadLetterQueue` |

Senders see a normal "accepted" disposition; no message ever accumulates on the
forwarding source.

## Forward live messages

```csharp
await host.Queues.CreateAsync(new QueueDescriptor { Name = "downstream" });
await host.Queues.CreateAsync(new QueueDescriptor
{
    Name = "ingress",
    ForwardTo = "downstream",
});

await sender.SendMessageAsync(new ServiceBusMessage("hi")); // hits "ingress" → lands at "downstream"
```

Receivers attached to `ingress` get nothing - the broker bypasses the source's available
pool entirely.

## Forward dead-lettered messages

```csharp
await host.Queues.CreateAsync(new QueueDescriptor { Name = "central-dlq" });
await host.Queues.CreateAsync(new QueueDescriptor
{
    Name = "orders",
    MaxDeliveryCount = 3,
    ForwardDeadLetteredMessagesTo = "central-dlq",
});
```

Whether a message dead-letters via explicit `DeadLetterMessageAsync`, max-delivery
exhaustion, or TTL expiry, the broker routes it to `central-dlq` instead of the local
`orders/$DeadLetterQueue`. The dead-letter source annotation
(`x-opt-deadletter-source = "orders"`) tells you where the message originated.

## Forward to a topic

`ForwardTo` can point at a topic - the broker fans out across the topic's subscriptions
(running each subscription's rule chain) and even honors subscription-level `ForwardTo`
recursively:

```csharp
await host.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "all-1" });
await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "all-2" });

await host.Queues.CreateAsync(new QueueDescriptor { Name = "ingress", ForwardTo = "events" });

// One send to "ingress" → fan-out to both subscriptions.
await sender.SendMessageAsync(new ServiceBusMessage("broadcast"));
```

## Subscription-level forwarding

```csharp
await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
{
    TopicName = "events",
    Name = "eu",
    ForwardTo = "eu-aggregate",
});
```

Messages that match `eu`'s rules skip the subscription's backing queue entirely and route
to `eu-aggregate`. Other subscriptions on the same topic are unaffected.

## Transfer dead-letter queue

Every queue and subscription has a transfer dead-letter sub-entity at
`<entity>/$Transfer/$DeadLetterQueue`, matching Azure Service Bus. When a forward hop
cannot be delivered, the message moves there instead of being dropped, stamped with
`DeadLetterReason`, `DeadLetterErrorDescription`, and the `x-opt-deadletter-source`
annotation:

| Forward failure | `DeadLetterReason` |
| --- | --- |
| Target queue or topic no longer exists | `MessagingEntityNotFound` |
| Target is `SendDisabled` / `Disabled` | `MessagingEntityDisabled` |
| Target is over its `MaxSizeInMegabytes` quota | `QuotaExceeded` |
| Chain exceeded the 4-hop cap | `MaxTransferHopCountExceeded` |

The message lands in the transfer DLQ of the entity **whose forward failed** - for a
chain A → B → C where C was deleted, that is B's. This covers `ForwardTo` and
`ForwardDeadLetteredMessagesTo` hops (from live receives, TTL expiration, and
management-plane dead-lettering alike).

Receive from it with the SDK's dedicated sub-queue option or by raw path:

```csharp
var receiver = client.CreateReceiver("orders",
    new ServiceBusReceiverOptions { SubQueue = SubQueue.TransferDeadLetter });
```

`TransferDeadLetterMessageCount` is reported on queue and subscription runtime
properties (ATOM/`Get*RuntimePropertiesAsync` and the JSON REST API), the entity's
size and quota include the transfer DLQ contents, and the Explorer shows a
Transfer DLQ tab on every queue and subscription. Purging an entity purges its
transfer DLQ along with it.

## Cycle protection

Auto-forwarding has a **4-hop chain cap**, matching Azure Service Bus. If A → B → A → B → …
the router stops at the 4th hop and moves the message to the transfer dead-letter queue
of the entity where the cap tripped, with reason `MaxTransferHopCountExceeded`.

Self-forwarding (`ForwardTo == Name`) is rejected at creation time with
`InvalidOperationException`.

## Validation

| Rule                                                         | When       | Error                            |
| ------------------------------------------------------------ | ---------- | -------------------------------- |
| `ForwardTo == Name`                                          | At create  | `InvalidOperationException`      |
| `ForwardDeadLetteredMessagesTo == Name`                      | At create  | `InvalidOperationException`      |
| Subscription `ForwardTo == BackingQueueName` or parent topic | At create  | `InvalidOperationException`      |
| Target entity doesn't exist                                  | At runtime | Transfer DLQ (`MessagingEntityNotFound`) |
| Target disabled or over quota                                | At runtime | Transfer DLQ (`MessagingEntityDisabled` / `QuotaExceeded`) |
| Chain > 4 hops                                               | At runtime | Transfer DLQ (`MaxTransferHopCountExceeded`) |

Target existence is checked **lazily** at runtime so `config.json` bootstrap can declare
queues + topics in any order without forcing a topological sort.

## Tests

- [`tests/OpenServiceBus.IntegrationTests/AutoForwardingTests.cs`](https://github.com/mauritsarissen/OpenServiceBus/blob/main/tests/OpenServiceBus.IntegrationTests/AutoForwardingTests.cs)
  - SDK-level: queue → queue, DLQ forwarding, chained queues, queue → topic fan-out,
    subscription-level forwarding, self-forward rejection, cycle drop.

## See also

- [Topics and Subscriptions](Topics-and-Subscriptions) - how subscription rules interact with `ForwardTo`.
