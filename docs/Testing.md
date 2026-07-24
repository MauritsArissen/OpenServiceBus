# Testing

`OpenServiceBus.Testing` is built specifically to be embedded inside your test fixtures -
zero containers, zero SQL, zero external processes. One disposable host, one ephemeral
port, every Service Bus feature available against the real Azure SDK.

## Install

```bash
dotnet add package OpenServiceBus.Testing
```

## Hello world

```csharp
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Testing;

[Fact]
public async Task SendAndReceive()
{
    await using var host = await OpenServiceBusTestHost.StartAsync();
    await host.CreateQueueAsync("orders");

    await using var client = new ServiceBusClient(host.ConnectionString);
    await client.CreateSender("orders").SendMessageAsync(new ServiceBusMessage("hello"));

    var receiver = client.CreateReceiver("orders");
    var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

    msg.Body.ToString().Should().Be("hello");
    await receiver.CompleteMessageAsync(msg);
}
```

The host disposes the listener + frees the port on `await using` exit. Run thousands of
these in parallel - each picks its own ephemeral port.

## Useful options

```csharp
await using var host = await OpenServiceBusTestHost.StartAsync(o =>
{
    // Fixed port - only useful for debugging against an external tool. Default = ephemeral.
    o.Port = 5672;

    // Drive TTL / lock-renewal / scheduled-message tests without Task.Delay.
    o.TimeProvider = new FakeTimeProvider();

    // Enforce SAS auth via $cbs put-token validation.
    o.RequireSasAuth = true;
    o.AdditionalSasKeys["worker"] = "secret-456";

    // Enable AMQP-over-WebSocket alongside plain TCP.
    o.EnableWebSocketBridge = true;

    // Swap in a different IMessageStore. The SQLite tests project uses this to run
    // the full SDK suite against a persistent backing store.
    o.StoreFactory = tp => new SqliteMessageStore(
        new SqliteStorageOptions { DataSource = ":memory:" }, tp, NullLogger<SqliteMessageStore>.Instance);
});
```

## Exposed surface

After `StartAsync` the host exposes the broker's internals directly so tests can poke at
state without going through the AMQP wire:

```csharp
host.ConnectionString          // for ServiceBusClient
host.WebSocketConnectionString // when EnableWebSocketBridge=true
host.AmqpUri                   // for low-level AMQPNetLite clients
host.Port

host.Queues                    // IQueueRegistry - host.Queues.CreateAsync(...)
host.Topics                    // ITopicRegistry - host.Topics.CreateTopicAsync(...) etc.
host.Store                     // IMessageStore - host.Store.CountAsync(...), Peek(...)
host.TimeProvider              // the time source the broker is using (e.g. FakeTimeProvider)
```

## Time-travel testing

The whole broker runs on a single `TimeProvider`. Pass `FakeTimeProvider` and you control
the clock:

```csharp
var time = new FakeTimeProvider();
await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = time);

await host.Queues.CreateAsync(new QueueDescriptor
{
    Name = "ttl-test",
    DefaultMessageTimeToLive = TimeSpan.FromMinutes(1),
    DeadLetteringOnMessageExpiration = true,
});

await using var client = new ServiceBusClient(host.ConnectionString);
await client.CreateSender("ttl-test").SendMessageAsync(new ServiceBusMessage("about to expire"));

time.Advance(TimeSpan.FromMinutes(2));
host.Store.ExpireMessages("ttl-test", time.GetUtcNow());

(await host.Store.CountAsync("ttl-test/$DeadLetterQueue")).Should().Be(1);
```

No `Task.Delay`. Tests run deterministically + fast.

Note: the test host runs the TTL sweeper and the scheduled-message activator, but **not**
the background lock-expiry sweeper the standalone host runs. Sweeps you drive from a fake
clock are explicit calls on the store:

```csharp
host.Store.ExpireMessages("q", time.GetUtcNow()); // TTL expiry
host.Store.ExpireLocks("q", time.GetUtcNow());    // peek-lock expiry -> redelivery
```

If a test needs a lock to lapse and the message to be redelivered, call `ExpireLocks`
after advancing the clock - waiting in real time will not release it.

## Parity testing

Want to prove your code works against both stores? Boot a second host with
`StoreFactory` pointing at the persistent variant and re-run the same scenarios:

```csharp
public static IEnumerable<object[]> StoreFactories => new[]
{
    new object[] { (Func<TimeProvider, IMessageStore>?)null },                          // in-memory default
    new object[] { (Func<TimeProvider, IMessageStore>?)(tp => new SqliteMessageStore(...)) }
};

[Theory]
[MemberData(nameof(StoreFactories))]
public async Task SendReceiveCompleteWorks(Func<TimeProvider, IMessageStore>? factory)
{
    await using var host = await OpenServiceBusTestHost.StartAsync(o => o.StoreFactory = factory);
    // ... same body for both stores
}
```

## See also

- [Architecture](Architecture) - what `OpenServiceBusTestHost` wires up internally.
- [Persistence](Persistence) - running tests against the SQLite store.
- [OpenTelemetry](OpenTelemetry) - capturing spans/metrics inside tests with `ActivityListener` / `MeterListener`.
