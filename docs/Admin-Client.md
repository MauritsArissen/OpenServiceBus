# SDK Admin Client (ATOM Management API)

The broker speaks the Azure Service Bus **ATOM-pub management protocol** - the HTTP surface
behind `ServiceBusAdministrationClient` - on the **same port as AMQP**. Queues, topics,
subscriptions, and rules can be created, inspected, updated, and deleted with the official
SDK admin client, using the exact same connection string as the data plane:

```csharp
var connectionString =
    "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

var admin = new ServiceBusAdministrationClient(connectionString);
await admin.CreateQueueAsync(new CreateQueueOptions("orders")
{
    MaxDeliveryCount = 5,
    LockDuration = TimeSpan.FromSeconds(30),
});

await using var client = new ServiceBusClient(connectionString);
await client.CreateSender("orders").SendMessageAsync(new ServiceBusMessage("hello"));
```

This also works out of the box against `OpenServiceBusTestHost`:

```csharp
await using var host = await OpenServiceBusTestHost.StartAsync();
var admin = new ServiceBusAdministrationClient(host.ConnectionString);
await admin.CreateQueueAsync("orders");
```

> **SDK version requirement:** `Azure.Messaging.ServiceBus` **7.20.1 or newer**. Older
> versions ignore `UseDevelopmentEmulator=true` in the admin client and dial
> `https://host:443` (7.18.x and below) or `http://host:80` (7.19.x) no matter what the
> connection string says. The data-plane `ServiceBusClient` is unaffected.

## How one port serves two protocols

The SDK derives **both** endpoints from the single `Endpoint=sb://host:port` value: AMQP for
`ServiceBusClient`, plain HTTP for `ServiceBusAdministrationClient` (when
`UseDevelopmentEmulator=true`). So the public port has to speak both.

A protocol front door owns the public port. Every AMQP connection starts with the 8-byte
protocol header `AMQP…`; no HTTP request line can. The front door peeks the first bytes of
each accepted connection and pipes it verbatim to the matching loopback backend:

```text
                        ┌─ "AMQP…" ──▶ AMQP listener (loopback)
client ──▶ :5672 sniff ─┤
                        └─ anything else ──▶ ATOM management server (loopback)
```

No TLS is involved (emulator mode), the pipe is transparent byte copying, and each side's
half-close is propagated so AMQP close handshakes and HTTP keep-alive behave exactly as on a
direct connection.

## Supported operations

| Operation | Notes |
| --- | --- |
| `CreateQueueAsync` / `CreateTopicAsync` | 409 `MessagingEntityAlreadyExists` on conflict, like Azure |
| `CreateSubscriptionAsync` (incl. the overload with `CreateRuleOptions`) | the piggybacked rule replaces the implicit `$Default` TrueFilter rule |
| `CreateRuleAsync` | `SqlRuleFilter`, `CorrelationRuleFilter`, `TrueRuleFilter`, `FalseRuleFilter` |
| `GetQueueAsync` / `GetTopicAsync` / `GetSubscriptionAsync` / `GetRuleAsync` | 404 `MessagingEntityNotFound` when missing |
| `QueueExistsAsync` / `TopicExistsAsync` / `SubscriptionExistsAsync` | |
| `GetQueuesAsync` / `GetTopicsAsync` / `GetSubscriptionsAsync` / `GetRulesAsync` | paged (`$skip`/`$top`); DLQ sub-entities and subscription backing queues are not listed |
| `UpdateQueueAsync` / `UpdateTopicAsync` / `UpdateSubscriptionAsync` / `UpdateRuleAsync` | in-place descriptor update; messages untouched; `RequiresSession` / `RequiresDuplicateDetection` are create-time-only (400, like Azure) |
| `DeleteQueueAsync` / `DeleteTopicAsync` / `DeleteSubscriptionAsync` / `DeleteRuleAsync` | deleting a topic tears down its subscriptions |
| `GetQueueRuntimePropertiesAsync` / `GetSubscriptionRuntimePropertiesAsync` / `GetTopicRuntimePropertiesAsync` | active / scheduled / dead-letter counts, size in bytes, created/updated timestamps |
| `GetNamespacePropertiesAsync` | reports a `Standard`-SKU messaging namespace |

Properties the broker enforces round-trip faithfully: `LockDuration`, `MaxDeliveryCount`,
`RequiresSession`, `RequiresDuplicateDetection`, `DuplicateDetectionHistoryTimeWindow`,
`DefaultMessageTimeToLive`, `DeadLetteringOnMessageExpiration`, `ForwardTo`,
`ForwardDeadLetteredMessagesTo`, and `UserMetadata`.

Properties the broker does not (yet) enforce are **accepted and returned with Azure's
defaults** so SDK parsers and IaC-style "ensure entity exists" code keep working:
`MaxSizeInMegabytes`, `AutoDeleteOnIdle`, `EnableBatchedOperations`, `EnablePartitioning`,
`Status` (always `Active`), and authorization rules. SQL rule **actions** round-trip and
are evaluated during fan-out - see
[Topics and Subscriptions](Topics-and-Subscriptions.md#sql-rule-actions).

## Authentication

By default the management surface mirrors emulator-mode permissive auth: any
`SharedAccessSignature` is accepted. When the AMQP listener runs with
`OpenServiceBus:Amqp:RequireSasAuth=true`, the same key store validates the
`Authorization` header of every management request; bad or missing tokens get a 401, which
the SDK surfaces as `UnauthorizedAccessException`.

## Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `OpenServiceBus:AtomManagement:Enabled` | `true` | Set `false` to give the AMQP listener the public port directly again (admin clients then get connection-refused, the pre-1.x behaviour) |

On `OpenServiceBusTestHost` the equivalent switch is
`OpenServiceBusTestHostOptions.EnableAtomManagement`.

## Interop notes

- The management surface is plain HTTP + ATOM XML, so `curl` works too:
  `curl "http://localhost:5672/orders?api-version=2021-05"`.
- The [Explorer UI](Explorer.md) manages entities through the real
  `ServiceBusAdministrationClient` against this API - the UI, the SDKs, and `curl` all
  share one management plane.
- The JSON REST API on port 5300 is unchanged and still serves `/health` and the
  Explorer's metrics sampling.

### Cross-SDK admin client status

The other SDKs implement the same protocol but (as of their latest releases, verified
2026-07-29) their admin clients cannot target a plaintext emulator endpoint yet - each
hardcodes `https://`:

| SDK | Version checked | Admin client vs emulator |
| --- | --- | --- |
| .NET `Azure.Messaging.ServiceBus` | 7.20.1 | ✅ works (7.20.1+ required) |
| JS `@azure/service-bus` | 7.9.5 | ❌ `serviceBusAtomManagementClient` builds `https://{endpoint}` |
| Python `azure-servicebus` | 7.14.3 | ❌ `_management_client.py` builds `https://` + namespace |
| Java `azure-messaging-servicebus` | 7.17.11 | ❌ dials `https://host:443` even with an explicit http `endpoint()` override |

The broker side is ready for all of them the moment those SDKs follow the .NET client's
lead. Until then, `tests/sdk-smoke/` exercises the ATOM surface from Node, Python, and
Java with plain HTTP (and from .NET with the native admin client), so the protocol stays
smoke-gated in CI from all four runtimes.
