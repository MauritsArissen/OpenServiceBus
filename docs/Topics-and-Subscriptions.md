# Topics and Subscriptions

Topics fan messages out to N subscriptions based on **filter rules**. Senders publish to
the topic name; receivers attach to `<topic>/Subscriptions/<sub>`. Each subscription has
its own backing queue with full peek-lock semantics - so you get TTL, DLQ, max-delivery,
sessions, and everything else per subscription.

## Creating

```csharp
await host.Topics.CreateTopicAsync(new TopicDescriptor
{
    Name = "events",
    DefaultMessageTimeToLive = TimeSpan.FromHours(1),
});

await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
{
    TopicName = "events",
    Name = "eu",
    LockDuration = TimeSpan.FromSeconds(60),
    MaxDeliveryCount = 10,
});
```

Every fresh subscription gets a `$Default` rule with a `TrueFilter` - matches everything,
matching Azure Service Bus's behaviour. Replace it or add more rules to filter.

## Filter rules

Four flavours, mirroring Service Bus:

### `TrueFilter` / `FalseFilter`

```csharp
await host.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
{
    TopicName = "events", SubscriptionName = "all",
    Name = "$Default", Filter = TrueFilter.Instance,
});
```

`FalseFilter` keeps a subscription quiescent without deleting it - handy for maintenance.

### `CorrelationFilter`

Property-equality match against any combination of system properties + application properties:

```csharp
new RuleDescriptor
{
    TopicName = "events", SubscriptionName = "orders",
    Name = "OrdersOnly",
    Filter = new CorrelationFilter
    {
        Subject = "OrderCreated",
        SessionId = "tenant-42",
        Properties = { ["region"] = "eu" },
    },
};
```

Empty fields are wildcards. All non-empty fields must match exactly. Faster than SQL
filters when you only need equality.

### `SqlFilter`

Subset of T-SQL covering the common cases:

```csharp
new SqlFilter("region IN ('eu', 'eu-west') AND priority >= 5 AND sys.Subject LIKE 'order%'")
```

Supports:

| Feature          | Example                                                                      |
| ---------------- | ---------------------------------------------------------------------------- |
| Comparisons      | `=` `!=` `<` `>` `<=` `>=`                                                   |
| Boolean          | `AND` `OR` `NOT`                                                             |
| Membership       | `IN (a, b, c)` / `NOT IN (...)`                                              |
| Pattern          | `LIKE 'foo%'`, `LIKE 'a_c'`, `NOT LIKE ...`                                  |
| Existence        | `IS NULL`, `IS NOT NULL`, `EXISTS(prop)` / `NOT EXISTS(prop)`                |
| Property scoping | `sys.MessageId`, `user.region`, or bare `region` (defaults to user-property) |
| Functions        | (none in v1 - keep it predictable)                                           |

Not supported (rejected or unavailable): arithmetic and string concatenation, `BETWEEN`,
the `ESCAPE` clause on `LIKE`, functions (`newid()`, `UPPER`, ...), parameterized filters,
and rule **actions** (`SET x = y` is accepted on the wire but not evaluated).

Property scoping note: `sys.*` refers to AMQP system properties (MessageId, CorrelationId,
Subject, To, ReplyTo, etc.); `user.*` and unscoped names look up `ApplicationProperties`.

## Sending to a topic

Just send - fan-out is server-side. The Azure SDK has no special API:

```csharp
var sender = client.CreateSender("events");
await sender.SendMessageAsync(new ServiceBusMessage("hello-eu")
{
    Subject = "OrderCreated",
    ApplicationProperties = { ["region"] = "eu", ["priority"] = 7 },
});
```

The broker evaluates every subscription's rules against the message. **Any rule matches**
inside a subscription is enough - a subscription with no rules matches nothing.

## Receiving from a subscription

```csharp
var receiver = client.CreateReceiver("events/Subscriptions/eu");
var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
await receiver.CompleteMessageAsync(msg);
```

Subscriptions are real queues under the hood (named `<topic>/Subscriptions/<name>`), so
all the standard `IMessageStore` operations work - dead-letter, abandon, defer, peek,
sessions, dedup, transactions.

## DLQ for a subscription

Same shape as queue DLQs, just nested:

```text
events/Subscriptions/eu/$DeadLetterQueue
```

Attach a receiver to that address and you can drain dead-lettered messages from this
specific subscription. The `messaging.servicebus.dead_letter_source` annotation on each
DLQ message tells you the original subscription.

## REST + Explorer

Topics, subscriptions, and rules are first-class in:

- The **REST management API** at `/topics`, `/topics/{topic}/subscriptions`,
  `/topics/{topic}/subscriptions/{sub}/rules`.
- The **[Explorer UI](Explorer)** - collapsible topic tree, per-subscription DLQ, rule
  editor with SQL / correlation / true / false variants.

## See also

- [Auto-Forwarding](Auto-Forwarding) - chain a subscription into another queue or topic.
- [Sessions](Sessions) - `RequiresSession` subscriptions for ordered, session-locked delivery.
