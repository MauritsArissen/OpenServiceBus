using Azure.Messaging.ServiceBus;

// Topic pub-sub with three subscriptions, each filtering messages differently:
//
//   all            - TrueFilter (auto-installed $Default rule). Sees every message.
//   eu-orders      - SQL filter:        region = 'eu' AND priority >= 5
//   high-priority  - Correlation filter: priority = 9
//
// The topic + subscriptions + rules are pre-declared by config.json so the SDK doesn't
// have to manage entities - it just sends and receives.

var connectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION")
    ?? "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

const string topic = "events";
string[] subs = ["all", "eu-orders", "high-priority"];

await using var client = new ServiceBusClient(connectionString);

// Three messages with different properties to exercise the rule matrix.
var messages = new[]
{
    new ServiceBusMessage("eu-low-priority")
    {
        MessageId = "m1",
        ApplicationProperties = { ["region"] = "eu", ["priority"] = 3 },
    },
    new ServiceBusMessage("eu-high-priority")
    {
        MessageId = "m2",
        ApplicationProperties = { ["region"] = "eu", ["priority"] = 9 },
    },
    new ServiceBusMessage("us-high-priority")
    {
        MessageId = "m3",
        ApplicationProperties = { ["region"] = "us", ["priority"] = 9 },
    },
};

Console.WriteLine($"Publishing 3 messages to topic '{topic}'…");
var sender = client.CreateSender(topic);
foreach (var msg in messages)
{
    await sender.SendMessageAsync(msg);
    Console.WriteLine($"  sent {msg.MessageId} region={msg.ApplicationProperties["region"]} priority={msg.ApplicationProperties["priority"]}");
}

Console.WriteLine();
Console.WriteLine("Draining each subscription (timeout 2s)…");
foreach (var sub in subs)
{
    Console.WriteLine($"\n[{sub}]");
    var receiver = client.CreateReceiver(topic, sub);
    while (true)
    {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        if (msg is null) break;
        Console.WriteLine($"  {msg.MessageId}  body=\"{msg.Body}\"");
        await receiver.CompleteMessageAsync(msg);
    }
}

Console.WriteLine();
Console.WriteLine("Expected distribution:");
Console.WriteLine("  all            → m1, m2, m3");
Console.WriteLine("  eu-orders      → m2          (region=eu AND priority>=5)");
Console.WriteLine("  high-priority  → m2, m3      (priority=9 via correlation filter)");
