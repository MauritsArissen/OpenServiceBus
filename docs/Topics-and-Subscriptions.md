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
| Arithmetic       | `+` `-` `*` `/` `%`, unary minus; `+` doubles as string concatenation        |
| Membership       | `IN (a, b, c)` / `NOT IN (...)`                                              |
| Pattern          | `LIKE 'foo%'`, `LIKE 'a_c'`, `NOT LIKE ...`, `LIKE '100!%' ESCAPE '!'`       |
| Existence        | `IS NULL`, `IS NOT NULL`, `EXISTS(prop)` / `NOT EXISTS(prop)`                |
| Property scoping | `sys.MessageId`, `user.region`, bare `region` (defaults to user-property), `[quoted-name]` / `"quoted name"` |
| Functions        | `newid()`, `property(name)` / `p(name)` (both also usable in rule actions)   |
| Parameters       | `priority >= @threshold` with values from `SqlRuleFilter.Parameters` / `SqlRuleAction.Parameters` |

Invalid expressions (parse errors, non-boolean top level, unknown functions, malformed
`ESCAPE`) are rejected at rule-creation time - the SDK surfaces `ArgumentException`, the
same shape real Service Bus produces - instead of being accepted and never matching.
A filter that throws at evaluation time (arithmetic on a string property, or - like
Azure - referencing a nonexistent `sys.` property) counts as a non-match for that
subscription; it never fails the publish. Property names are case-insensitive, and
`property(...)`/`p(...)` accepts any string-valued expression as the name. Numeric coercion covers
every AMQP integral and fractional type, so a `4` sent from any SDK (long from .NET,
uint from Node.js, int from Java) compares and computes identically.

Literals cover strings (`''` escapes a quote), integers, decimals, scientific notation
(`1.5E3`), and TRUE/FALSE/NULL. `LIKE` patterns and escapes may be any string-valued
expression. Numeric comparison and equality follow C# implicit-conversion semantics
across every AMQP numeric type. Parameterized filters bind at rule creation and reach
the broker through the admin client (ATOM); the SDK's AMQP `ServiceBusRuleManager` does
not transmit parameters - an SDK limitation that applies identically against real Azure.

`BETWEEN` is not part of the Service Bus SQL grammar and is rejected, same as Azure.

Property scoping note: `sys.*` refers to AMQP system properties (MessageId, CorrelationId,
Subject, To, ReplyTo, etc.); `user.*` and unscoped names look up `ApplicationProperties`.

## SQL rule actions

A rule can carry an **action** alongside its filter: a semicolon-separated list of
`SET`/`REMOVE` statements that mutates the matched subscription's copy of the message
before it lands - other subscriptions are unaffected. Works through the SDK's
`ServiceBusRuleManager`, `ServiceBusAdministrationClient`, and `config.json`:

```csharp
await ruleManager.CreateRuleAsync(new CreateRuleOptions("tag", new SqlRuleFilter("priority > 5"))
{
    Action = new SqlRuleAction("SET sys.Label = 'high'; SET counter = counter + 1; REMOVE debug"),
});
```

Semantics:

- Statements apply **sequentially**; later statements see the results of earlier ones
  (`SET a = 1; SET b = a + 1` yields `b = 2`).
- Value expressions use the full filter grammar above, including arithmetic and string
  concatenation, evaluated against the message's current properties.
- `SET sys.X` may target the writable system properties: `Label`/`Subject`,
  `CorrelationId`, `To`, `ReplyTo`, `ReplyToSessionId`, `ContentType`. Anything else
  (`sys.MessageId`, `sys.SessionId`, ...) is rejected at rule-creation time, as are
  malformed actions. `REMOVE` applies to application properties only; use
  `SET sys.X = NULL` to clear a system property.
- When several rules on one subscription match the same message, the first matching rule
  in name order provides the action (one copy is delivered either way).
- A runtime evaluation failure (e.g. arithmetic on a non-numeric property) delivers the
  copy **unmodified** rather than losing it; the broker logs a warning.

In `config.json`, add an `Action` next to the filter payload:

```json
{ "Name": "tag", "Properties": { "FilterType": "Sql",
  "SqlFilter": { "SqlExpression": "priority > 5" },
  "Action": { "SqlExpression": "SET sys.Label = 'high'" } } }
```

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
