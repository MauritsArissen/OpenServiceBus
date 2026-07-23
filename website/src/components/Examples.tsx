import { useState } from "react";
import CopyButton from "./CopyButton";

type Example = {
  id: string;
  label: string;
  blurb: string;
  language: string;
  code: string;
};

const EXAMPLES: Example[] = [
  {
    id: "docker",
    label: "Docker",
    blurb:
      "Run the broker, management REST API, and Explorer UI in one Alpine container. Best when you want all three reachable from outside your test process.",
    language: "bash",
    code: `# Start the broker, management REST API, and Explorer UI together.
docker run --rm \\
  -p 5672:5672 \\   # AMQP broker
  -p 5300:5300 \\   # Management REST API
  -p 5400:5400 \\   # Explorer UI
  mauritsarissen/openservicebus:latest

# Point the Azure SDK at:
#   Endpoint=sb://localhost;SharedAccessKeyName=x;SharedAccessKey=x;UseDevelopmentEmulator=true
#
# Open the Explorer:
#   http://localhost:5400`,
  },
  {
    id: "xunit",
    label: "xUnit fixture",
    blurb:
      "Embed the broker directly in your test process via the OpenServiceBus.Testing NuGet package. No Docker, no external dependencies, ~50ms startup.",
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
  {
    id: "dotnet",
    label: ".NET console",
    blurb:
      "Plain old console app talking to a broker started via Docker. Same code you'd write against the real Azure Service Bus - only the connection string changes.",
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

if (msg is null)
{
    Console.WriteLine("No messages.");
    return;
}

Console.WriteLine($"Received: {msg.Body}");
await receiver.CompleteMessageAsync(msg);`,
  },
  {
    id: "functions",
    label: "Azure Functions",
    blurb:
      "Isolated-worker Functions app with a ServiceBusTrigger pointed at OpenServiceBus. Verified end-to-end in the project's integration test suite.",
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
  {
    id: "sessions",
    label: "Sessions & scheduling",
    blurb:
      "Session-enabled queues give you strict FIFO per key with concurrency across keys, and the broker holds scheduled messages until their due time. Accept-next-session parks server-side until a session appears - exactly like Azure.",
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
    Console.WriteLine($"[session {args.SessionId}] {args.Message.Body}");
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
    id: "topics",
    label: "Topics & SQL filters",
    blurb:
      "Publish once, let the broker fan out. Subscription rules run server-side, so each worker only ever receives what its SQL filter matches - shown here creating entities through the OpenServiceBus.Testing host.",
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
    id: "timetravel",
    label: "Time travel",
    blurb:
      "The entire broker runs on an injected TimeProvider. Hand it a FakeTimeProvider and scheduled messages, TTL, lock expiry, and dedup windows become instant, deterministic tests - no Task.Delay anywhere.",
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
  {
    id: "novabank",
    label: "Full app: NovaBank",
    blurb:
      "A complete event-driven banking API - made entirely as an example. Duplicate-detected transfers, session payments, scheduled standing orders, SQL-filtered fraud/audit/notification fan-out, DLQ inspection, Swagger UI, and 79 tests that run the real app on the embedded broker in seconds.",
    language: "bash",
    code: `# samples/NovaBank - a full event-driven bank, built 100% against
# Azure.Messaging.ServiceBus. Only the connection string decides whether
# it talks to Azure or to OpenServiceBus.
#
#   - async transfers on a duplicate-detected queue (Idempotency-Key = MessageId)
#   - session payments: per-account FIFO + broker-held standing orders
#   - topic fan-out with SQL filters: audit / fraud (amount >= 10000) / notifications
#   - automatic account freeze, dead-letter inspection API, Swagger UI
#   - 79 tests: the real API + embedded broker, ~3 seconds, no Docker

git clone https://github.com/mauritsarissen/OpenServiceBus
cd OpenServiceBus/samples/NovaBank

docker compose up -d --build                                  # broker + Explorer UI
dotnet run --project src/NovaBank.Api --launch-profile Local  # Swagger at :5080

dotnet test                                                   # the whole bank, embedded`,
  },
];

export default function Examples() {
  const [activeId, setActiveId] = useState(EXAMPLES[0].id);
  const active = EXAMPLES.find((e) => e.id === activeId)!;

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
        <div className="flex items-center justify-between gap-3 px-4 sm:px-5 pt-4 sm:pt-5 pb-3">
          <div className="flex items-center gap-2 min-w-0">
            <span className="h-2.5 w-2.5 rounded-full bg-red-500/70 shrink-0" />
            <span className="h-2.5 w-2.5 rounded-full bg-amber-500/70 shrink-0" />
            <span className="h-2.5 w-2.5 rounded-full bg-emerald-500/70 shrink-0" />
            <span className="ml-2 text-xs text-neutral-500 font-mono truncate">{active.language}</span>
          </div>
          <CopyButton text={active.code} />
        </div>
        <pre className="overflow-x-auto px-4 sm:px-5 pb-4 sm:pb-5 font-mono text-[12.5px] sm:text-sm leading-relaxed text-neutral-100">
          <Code text={active.code} />
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
  "using",
  "public",
  "private",
  "static",
  "async",
  "await",
  "class",
  "new",
  "var",
  "const",
  "return",
  "if",
  "else",
  "true",
  "false",
  "null",
  "void",
  "Task",
  "string",
  "int",
  "bool",
]);

function highlight(line: string) {
  // Comments - anything after // (bash and C# both use this); also full-line # for shell.
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
