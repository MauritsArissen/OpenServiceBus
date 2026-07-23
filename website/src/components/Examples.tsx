import { useState } from "react";
import CopyButton from "./CopyButton";

// Every example can carry one variant per SDK stack. Examples with a single variant
// (shell recipes, .NET-only Testing-package features) render without the toggle.
type Sdk = "dotnet" | "node" | "java" | "python" | "shell";

const SDK_LABELS: Record<Sdk, string> = {
  dotnet: ".NET",
  node: "Node.js",
  java: "Java",
  python: "Python",
  shell: "Shell",
};

type Variant = {
  sdk: Sdk;
  language: string;
  code: string;
};

type Example = {
  id: string;
  label: string;
  blurb: string;
  variants: Variant[];
};

const EXAMPLES: Example[] = [
  {
    id: "docker",
    label: "Docker",
    blurb:
      "Run the broker, management REST API, and Explorer UI in one Alpine container. Best when you want all three reachable from outside your test process.",
    variants: [
      {
        sdk: "shell",
        language: "bash",
        code: `# Start the broker, management REST API, and Explorer UI together.
docker run --rm \\
  -p 5672:5672 \\   # AMQP broker
  -p 5300:5300 \\   # Management REST API
  -p 5400:5400 \\   # Explorer UI
  mauritsarissen/openservicebus:latest

# Point any Azure Service Bus SDK (.NET, Node.js, Java, Python) at:
#   Endpoint=sb://localhost;SharedAccessKeyName=x;SharedAccessKey=x;UseDevelopmentEmulator=true
#
# Open the Explorer:
#   http://localhost:5400`,
      },
    ],
  },
  {
    id: "console",
    label: "Send & receive",
    blurb:
      "The same send / peek-lock receive / complete flow in every official SDK. Identical code against the real Azure Service Bus - only the connection string changes. All four stacks are exercised against OpenServiceBus in CI on every release.",
    variants: [
      {
        sdk: "dotnet",
        language: "csharp",
        code: `using Azure.Messaging.ServiceBus;

const string conn =
    "Endpoint=sb://localhost;" +
    "SharedAccessKeyName=RootManageSharedAccessKey;" +
    "SharedAccessKey=anykey;" +
    "UseDevelopmentEmulator=true";

await using var client = new ServiceBusClient(conn);

// --- Sender ---
var sender = client.CreateSender("orders");
await sender.SendMessageAsync(new ServiceBusMessage("hello"));
Console.WriteLine("Sent.");

// --- Receiver (peek-lock) ---
var receiver = client.CreateReceiver("orders");
var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

if (msg is not null)
{
    Console.WriteLine("Received: " + msg.Body);
    await receiver.CompleteMessageAsync(msg);
}`,
      },
      {
        sdk: "node",
        language: "javascript",
        code: `import { ServiceBusClient } from "@azure/service-bus";

const conn =
  "Endpoint=sb://localhost;" +
  "SharedAccessKeyName=RootManageSharedAccessKey;" +
  "SharedAccessKey=anykey;" +
  "UseDevelopmentEmulator=true";

const client = new ServiceBusClient(conn);

// --- Sender ---
const sender = client.createSender("orders");
await sender.sendMessages({ body: "hello" });
console.log("Sent.");

// --- Receiver (peek-lock) ---
const receiver = client.createReceiver("orders");
const [msg] = await receiver.receiveMessages(1, { maxWaitTimeInMs: 5000 });

if (msg) {
  console.log("Received: " + msg.body);
  await receiver.completeMessage(msg);
}

await client.close();`,
      },
      {
        sdk: "java",
        language: "java",
        code: `String conn =
    "Endpoint=sb://localhost;"
    + "SharedAccessKeyName=RootManageSharedAccessKey;"
    + "SharedAccessKey=anykey;"
    + "UseDevelopmentEmulator=true";

// --- Sender ---
ServiceBusSenderClient sender = new ServiceBusClientBuilder()
    .connectionString(conn)
    .sender().queueName("orders").buildClient();
sender.sendMessage(new ServiceBusMessage("hello"));
System.out.println("Sent.");

// --- Receiver (peek-lock) ---
ServiceBusReceiverClient receiver = new ServiceBusClientBuilder()
    .connectionString(conn)
    .receiver().queueName("orders").disableAutoComplete().buildClient();

for (ServiceBusReceivedMessage msg : receiver.receiveMessages(1, Duration.ofSeconds(5))) {
    System.out.println("Received: " + msg.getBody());
    receiver.complete(msg);
}`,
      },
      {
        sdk: "python",
        language: "python",
        code: `from azure.servicebus import ServiceBusClient, ServiceBusMessage

conn = (
    "Endpoint=sb://localhost;"
    "SharedAccessKeyName=RootManageSharedAccessKey;"
    "SharedAccessKey=anykey;"
    "UseDevelopmentEmulator=true"
)

with ServiceBusClient.from_connection_string(conn) as client:
    # --- Sender ---
    with client.get_queue_sender("orders") as sender:
        sender.send_messages(ServiceBusMessage("hello"))
        print("Sent.")

    # --- Receiver (peek-lock) ---
    with client.get_queue_receiver("orders", max_wait_time=5) as receiver:
        for msg in receiver.receive_messages(max_message_count=1):
            print("Received:", str(msg))
            receiver.complete_message(msg)`,
      },
    ],
  },
  {
    id: "xunit",
    label: "xUnit fixture",
    blurb:
      "Embed the broker directly in your test process via the OpenServiceBus.Testing NuGet package (.NET only). No Docker, no external dependencies, ~50ms startup. Other stacks test against the Docker container instead.",
    variants: [
      {
        sdk: "dotnet",
        language: "csharp",
        code: `using Azure.Messaging.ServiceBus;
using OpenServiceBus.Testing;

public class OrderProcessorTests : IAsyncLifetime
{
    private OpenServiceBusTestHost _host = null!;

    public async Task InitializeAsync()
    {
        // Spins up an in-memory broker on a random free port and returns
        // a ready-to-use connection string. ~50ms.
        _host = await OpenServiceBusTestHost.StartAsync();
        await _host.CreateQueueAsync("orders");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task SendAndReceive_RoundTrip()
    {
        await using var client = new ServiceBusClient(_host.ConnectionString);

        var sender = client.CreateSender("orders");
        await sender.SendMessageAsync(new ServiceBusMessage("order-42"));

        var receiver = client.CreateReceiver("orders");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("order-42", msg.Body.ToString());
        await receiver.CompleteMessageAsync(msg);
    }
}`,
      },
    ],
  },
  {
    id: "functions",
    label: "Azure Functions",
    blurb:
      "Isolated-worker Functions app with a ServiceBusTrigger pointed at OpenServiceBus. Verified end-to-end in the project's integration test suite.",
    variants: [
      {
        sdk: "dotnet",
        language: "csharp",
        code: `using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

public class OrderTrigger
{
    [Function(nameof(OrderTrigger))]
    public async Task Run(
        [ServiceBusTrigger("orders", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        FunctionContext context)
    {
        var logger = context.GetLogger<OrderTrigger>();
        logger.LogInformation("Processing: {Body}", message.Body);

        // ... your business logic ...

        await actions.CompleteMessageAsync(message);
    }
}

// local.settings.json
// {
//   "Values": {
//     "AzureWebJobsStorage": "UseDevelopmentStorage=true",
//     "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
//     "ServiceBusConnection":
//       "Endpoint=sb://localhost;SharedAccessKeyName=x;SharedAccessKey=x;UseDevelopmentEmulator=true"
//   }
// }`,
      },
    ],
  },
  {
    id: "sessions",
    label: "Sessions & scheduling",
    blurb:
      "Session-enabled queues give you strict FIFO per key with concurrency across keys, and the broker holds scheduled messages until their due time. Accept-next-session parks server-side until a session appears - exactly like Azure.",
    variants: [
      {
        sdk: "dotnet",
        language: "csharp",
        code: `using Azure.Messaging.ServiceBus;

// SessionId is your partition key: strict FIFO per key, parallel across keys.
var processor = client.CreateSessionProcessor("payments", new ServiceBusSessionProcessorOptions
{
    MaxConcurrentSessions = 4,
    MaxConcurrentCallsPerSession = 1,
});
processor.ProcessMessageAsync += args =>
{
    // Messages for one account always arrive in order.
    Console.WriteLine("[" + args.SessionId + "] " + args.Message.Body);
    return Task.CompletedTask;
};
await processor.StartProcessingAsync();

var sender = client.CreateSender("payments");

// Ordered work for one account...
await sender.SendMessageAsync(new ServiceBusMessage("rent")   { SessionId = "account-1" });
await sender.SendMessageAsync(new ServiceBusMessage("energy") { SessionId = "account-1" });

// ...and a standing order the BROKER holds until the due time -
// it survives app restarts, unlike an in-process timer.
await sender.ScheduleMessageAsync(
    new ServiceBusMessage("monthly savings") { SessionId = "account-1" },
    DateTimeOffset.UtcNow.AddDays(30));`,
      },
      {
        sdk: "node",
        language: "javascript",
        code: `import { ServiceBusClient } from "@azure/service-bus";

const client = new ServiceBusClient(conn);
const sender = client.createSender("payments");

// sessionId is your partition key: strict FIFO per key, parallel across keys.
await sender.sendMessages({ body: "rent",   sessionId: "account-1" });
await sender.sendMessages({ body: "energy", sessionId: "account-1" });

// A standing order the BROKER holds until the due time -
// it survives app restarts, unlike an in-process timer.
await sender.scheduleMessages(
  { body: "monthly savings", sessionId: "account-1" },
  new Date(Date.now() + 30 * 24 * 3600 * 1000));

// Accept the session and receive ITS messages, in order.
const session = await client.acceptSession("payments", "account-1");
const msgs = await session.receiveMessages(2, { maxWaitTimeInMs: 5000 });
for (const msg of msgs) {
  console.log("[" + session.sessionId + "] " + msg.body);
  await session.completeMessage(msg);
}
await session.close();`,
      },
      {
        sdk: "java",
        language: "java",
        code: `ServiceBusSenderClient sender = new ServiceBusClientBuilder()
    .connectionString(conn)
    .sender().queueName("payments").buildClient();

// setSessionId is your partition key: strict FIFO per key, parallel across keys.
ServiceBusMessage rent = new ServiceBusMessage("rent");
rent.setSessionId("account-1");
sender.sendMessage(rent);

// A standing order the BROKER holds until the due time.
ServiceBusMessage savings = new ServiceBusMessage("monthly savings");
savings.setSessionId("account-1");
sender.scheduleMessage(savings, OffsetDateTime.now().plusDays(30));

// Accept the session and receive ITS messages, in order.
ServiceBusSessionReceiverClient sessionClient = new ServiceBusClientBuilder()
    .connectionString(conn)
    .sessionReceiver().queueName("payments").disableAutoComplete().buildClient();

try (ServiceBusReceiverClient session = sessionClient.acceptSession("account-1")) {
    for (ServiceBusReceivedMessage msg : session.receiveMessages(2, Duration.ofSeconds(5))) {
        System.out.println("[" + msg.getSessionId() + "] " + msg.getBody());
        session.complete(msg);
    }
}`,
      },
      {
        sdk: "python",
        language: "python",
        code: `from datetime import datetime, timedelta, timezone
from azure.servicebus import ServiceBusClient, ServiceBusMessage

with ServiceBusClient.from_connection_string(conn) as client:
    with client.get_queue_sender("payments") as sender:
        # session_id is your partition key: strict FIFO per key.
        sender.send_messages(ServiceBusMessage("rent",   session_id="account-1"))
        sender.send_messages(ServiceBusMessage("energy", session_id="account-1"))

        # A standing order the BROKER holds until the due time.
        due = datetime.now(timezone.utc) + timedelta(days=30)
        sender.schedule_messages(
            ServiceBusMessage("monthly savings", session_id="account-1"), due)

    # Accept the session and receive ITS messages, in order.
    with client.get_queue_receiver("payments", session_id="account-1",
                                   max_wait_time=5) as session:
        for msg in session.receive_messages(max_message_count=2):
            print("[" + msg.session_id + "]", str(msg))
            session.complete_message(msg)`,
      },
    ],
  },
  {
    id: "topics",
    label: "Topics & SQL filters",
    blurb:
      "Publish once, let the broker fan out. Subscription rules run server-side, so each worker only ever receives what its SQL filter matches. Entities and rules come from config.json (all SDKs) or the .NET Testing host.",
    variants: [
      {
        sdk: "dotnet",
        language: "csharp",
        code: `using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;

await host.Topics.CreateTopicAsync(new TopicDescriptor { Name = "bank-events" });
await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor
    { TopicName = "bank-events", Name = "fraud" });

// Replace the auto-created match-all rule with a SQL filter (same trick as Azure:
// a fresh subscription always starts with a $Default TrueFilter).
await host.Topics.CreateOrReplaceRuleAsync(new RuleDescriptor
{
    TopicName = "bank-events",
    SubscriptionName = "fraud",
    Name = "$Default",
    Filter = new SqlFilter("amount >= 10000"),
});

// Publish with application properties the filter can see.
var evt = new ServiceBusMessage("transfer.completed");
evt.ApplicationProperties["amount"] = 25_000d;
await client.CreateSender("bank-events").SendMessageAsync(evt);

// Only matching events ever reach this receiver - the broker filtered for you.
var fraud = client.CreateReceiver("bank-events", "fraud");
var alert = await fraud.ReceiveMessageAsync(TimeSpan.FromSeconds(2));`,
      },
      {
        sdk: "node",
        language: "javascript",
        code: `// Topic + filtered subscription declared in the container's config.json:
//   topic "bank-events", subscription "fraud",
//   rule $Default: SqlFilter "amount >= 10000"

import { ServiceBusClient } from "@azure/service-bus";
const client = new ServiceBusClient(conn);

// Publish with application properties the filter can see.
const sender = client.createSender("bank-events");
await sender.sendMessages({
  body: "transfer.completed",
  applicationProperties: { amount: 25000 },
});

// Only matching events ever reach this receiver - the broker filtered for you.
const fraud = client.createReceiver("bank-events", "fraud");
const [alert] = await fraud.receiveMessages(1, { maxWaitTimeInMs: 2000 });
if (alert) {
  console.log("fraud alert: " + alert.body);
  await fraud.completeMessage(alert);
}`,
      },
      {
        sdk: "java",
        language: "java",
        code: `// Topic + filtered subscription declared in the container's config.json:
//   topic "bank-events", subscription "fraud",
//   rule $Default: SqlFilter "amount >= 10000"

// Publish with application properties the filter can see.
ServiceBusSenderClient sender = new ServiceBusClientBuilder()
    .connectionString(conn)
    .sender().topicName("bank-events").buildClient();

ServiceBusMessage evt = new ServiceBusMessage("transfer.completed");
evt.getApplicationProperties().put("amount", 25000.0);
sender.sendMessage(evt);

// Only matching events ever reach this receiver - the broker filtered for you.
ServiceBusReceiverClient fraud = new ServiceBusClientBuilder()
    .connectionString(conn)
    .receiver().topicName("bank-events").subscriptionName("fraud")
    .disableAutoComplete().buildClient();

for (ServiceBusReceivedMessage alert : fraud.receiveMessages(1, Duration.ofSeconds(2))) {
    System.out.println("fraud alert: " + alert.getBody());
    fraud.complete(alert);
}`,
      },
      {
        sdk: "python",
        language: "python",
        code: `# Topic + filtered subscription declared in the container's config.json:
#   topic "bank-events", subscription "fraud",
#   rule $Default: SqlFilter "amount >= 10000"

from azure.servicebus import ServiceBusClient, ServiceBusMessage

with ServiceBusClient.from_connection_string(conn) as client:
    # Publish with application properties the filter can see.
    with client.get_topic_sender("bank-events") as sender:
        sender.send_messages(ServiceBusMessage(
            "transfer.completed",
            application_properties={"amount": 25000}))

    # Only matching events ever reach this receiver.
    with client.get_subscription_receiver("bank-events", "fraud",
                                          max_wait_time=2) as fraud:
        for alert in fraud.receive_messages(max_message_count=1):
            print("fraud alert:", str(alert))
            fraud.complete_message(alert)`,
      },
    ],
  },
  {
    id: "timetravel",
    label: "Time travel",
    blurb:
      "The entire broker runs on an injected TimeProvider (.NET Testing package). Hand it a FakeTimeProvider and scheduled messages, TTL, lock expiry, and dedup windows become instant, deterministic tests - no Task.Delay anywhere.",
    variants: [
      {
        sdk: "dotnet",
        language: "csharp",
        code: `using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Testing;

var clock = new FakeTimeProvider();
await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
await host.CreateQueueAsync("standing-orders");

await using var client = new ServiceBusClient(host.ConnectionString);

// Schedule a payment a month out.
await client.CreateSender("standing-orders").ScheduleMessageAsync(
    new ServiceBusMessage("monthly rent"),
    clock.GetUtcNow().AddDays(30));

// A month passes in a microsecond.
clock.Advance(TimeSpan.FromDays(31));

var receiver = client.CreateReceiver("standing-orders");
var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
// msg.Body == "monthly rent" - the whole test runs in milliseconds.`,
      },
    ],
  },
  {
    id: "novabank",
    label: "Full app: NovaBank",
    blurb:
      "A complete event-driven banking API - made entirely as an example. Duplicate-detected transfers, session payments, scheduled standing orders, SQL-filtered fraud/audit/notification fan-out, DLQ inspection, Swagger UI, and 79 tests that run the real app on the embedded broker in seconds.",
    variants: [
      {
        sdk: "shell",
        language: "bash",
        code: `# samples/dotnet/NovaBank - a full event-driven bank, built 100% against
# Azure.Messaging.ServiceBus. Only the connection string decides whether
# it talks to Azure or to OpenServiceBus.
#
#   - async transfers on a duplicate-detected queue (Idempotency-Key = MessageId)
#   - session payments: per-account FIFO + broker-held standing orders
#   - topic fan-out with SQL filters: audit / fraud (amount >= 10000) / notifications
#   - automatic account freeze, dead-letter inspection API, Swagger UI
#   - 79 tests: the real API + embedded broker, ~3 seconds, no Docker

git clone https://github.com/mauritsarissen/OpenServiceBus
cd OpenServiceBus/samples/dotnet/NovaBank

docker compose up -d                                          # broker + Explorer UI
dotnet run --project src/NovaBank.Api --launch-profile Local  # Swagger at :5080

dotnet test                                                   # the whole bank, embedded`,
      },
    ],
  },
];

export default function Examples() {
  const [activeId, setActiveId] = useState(EXAMPLES[0].id);
  // The chosen SDK persists across example tabs; examples that don't have it
  // fall back to their first variant.
  const [preferredSdk, setPreferredSdk] = useState<Sdk>("dotnet");

  const active = EXAMPLES.find((e) => e.id === activeId)!;
  const variant = active.variants.find((v) => v.sdk === preferredSdk) ?? active.variants[0];

  return (
    <section className="py-12 w-full min-w-0">
      {/* Tab strip - w-full + grid (not flex) so the layout never tracks tab-label
          length or the code box width. Equal columns on mobile, content-sized on sm+. */}
      <div className="grid grid-cols-2 sm:flex sm:flex-wrap gap-1 rounded-lg border border-neutral-800 bg-neutral-900/40 p-1 mb-6 w-full sm:w-fit sm:max-w-full">
        {EXAMPLES.map((ex) => (
          <button
            key={ex.id}
            onClick={() => setActiveId(ex.id)}
            className={`px-3 sm:px-3.5 py-2 rounded-md text-sm font-medium transition text-center ${
              activeId === ex.id
                ? "bg-neutral-800 text-white"
                : "text-neutral-400 hover:text-neutral-100 hover:bg-neutral-900"
            }`}
          >
            {ex.label}
          </button>
        ))}
      </div>

      {/* Blurb - height varies per tab but width is locked by the section. */}
      <p className="mb-4 text-neutral-400 max-w-2xl">{active.blurb}</p>

      {/* Code block - w-full + overflow-hidden on the outer card guarantees the
          card is exactly the section width, no matter how wide the code is.
          The inner <pre> with overflow-x-auto then takes the internal horizontal
          scroll. The card no longer breathes in/out as tabs switch. */}
      <div className="w-full rounded-xl border border-neutral-800 bg-neutral-900/60 overflow-hidden">
        <div className="flex flex-col gap-2 px-4 sm:px-5 pt-4 sm:pt-5 pb-3 sm:flex-row sm:items-center sm:justify-between sm:gap-3">
          <div className="flex items-center gap-2 min-w-0">
            <span className="h-2.5 w-2.5 rounded-full bg-red-500/70 shrink-0" />
            <span className="h-2.5 w-2.5 rounded-full bg-amber-500/70 shrink-0" />
            <span className="h-2.5 w-2.5 rounded-full bg-emerald-500/70 shrink-0" />
            <span className="ml-2 text-xs text-neutral-500 font-mono truncate">{variant.language}</span>
          </div>
          <div className="flex items-center gap-2 min-w-0 justify-between sm:justify-end">
            {active.variants.length > 1 && (
              <div className="flex flex-wrap gap-0.5 rounded-md border border-neutral-800 bg-neutral-900 p-0.5">
                {active.variants.map((v) => (
                  <button
                    key={v.sdk}
                    onClick={() => setPreferredSdk(v.sdk)}
                    className={`px-2 py-1 rounded text-[11px] font-semibold transition ${
                      variant.sdk === v.sdk
                        ? "bg-violet-500/20 text-violet-300"
                        : "text-neutral-500 hover:text-neutral-200"
                    }`}
                  >
                    {SDK_LABELS[v.sdk]}
                  </button>
                ))}
              </div>
            )}
            <CopyButton text={variant.code} />
          </div>
        </div>
        <pre className="overflow-x-auto px-4 sm:px-5 pb-4 sm:pb-5 font-mono text-[12.5px] sm:text-sm leading-relaxed text-neutral-100">
          <Code text={variant.code} />
        </pre>
      </div>
    </section>
  );
}

// Tiny manual highlighter - comments grey, strings green, keywords violet.
// Keeps the bundle small (no Shiki/Prism) and works fine for the short snippets we ship.
function Code({ text }: { text: string }) {
  const lines = text.split("\n");
  return (
    <>
      {lines.map((line, i) => (
        <div key={i}>{highlight(line)}{"\n"}</div>
      ))}
    </>
  );
}

const KEYWORDS = new Set([
  // shared / C#
  "using", "public", "private", "static", "async", "await", "class", "new", "var",
  "const", "return", "if", "else", "true", "false", "null", "void", "Task",
  "string", "int", "bool", "is", "not", "foreach", "in", "try", "catch", "finally",
  // JavaScript
  "import", "from", "export", "let", "of", "function",
  // Java
  "String", "boolean", "final", "package", "extends", "implements", "throws", "double",
  // Python
  "def", "with", "as", "for", "print", "None", "True", "False", "self", "lambda",
]);

function highlight(line: string) {
  // Comments - anything after // (C#/JS/Java); full-line # for shell and Python.
  const commentSplit = line.match(/^(.*?)(\/\/.*|#.*)?$/);
  const codePart = commentSplit?.[1] ?? line;
  const commentPart = commentSplit?.[2] ?? "";

  // Tokenize the non-comment part naively: strings ("...") + word boundaries.
  const tokens: React.ReactNode[] = [];
  const re = /("(?:[^"\\]|\\.)*"|\b[A-Za-z_][A-Za-z0-9_]*\b|.)/g;
  let m: RegExpExecArray | null;
  let key = 0;
  while ((m = re.exec(codePart)) !== null) {
    const tok = m[0];
    if (tok.startsWith('"')) {
      tokens.push(<span key={key++} className="text-emerald-400">{tok}</span>);
    } else if (KEYWORDS.has(tok)) {
      tokens.push(<span key={key++} className="text-violet-300">{tok}</span>);
    } else {
      tokens.push(<span key={key++}>{tok}</span>);
    }
  }
  return (
    <>
      {tokens}
      {commentPart && <span className="text-neutral-500">{commentPart}</span>}
    </>
  );
}
