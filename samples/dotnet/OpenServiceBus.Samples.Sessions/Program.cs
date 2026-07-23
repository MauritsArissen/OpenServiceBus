using Azure.Messaging.ServiceBus;

// Sessions: per-session FIFO + exclusive session ownership.
//
// We send 2 tenants × 3 messages = 6 total, interleaved on the wire. Then we spin up two
// "workers" that each call AcceptNextSessionAsync - the broker hands one tenant to each
// worker, FIFO-ordered within each session. Cross-tenant order is independent.

var connectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION")
    ?? "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

const string queue = "sessioned";

await using var client = new ServiceBusClient(connectionString);

// Interleave sends across two sessions so the broker has to keep them in order per session.
Console.WriteLine($"Sending interleaved messages to '{queue}'…");
var sender = client.CreateSender(queue);
for (var i = 1; i <= 3; i++)
{
    await sender.SendMessageAsync(new ServiceBusMessage($"tenant-A msg {i}") { SessionId = "tenant-A", MessageId = $"A-{i}" });
    await sender.SendMessageAsync(new ServiceBusMessage($"tenant-B msg {i}") { SessionId = "tenant-B", MessageId = $"B-{i}" });
}

Console.WriteLine();
Console.WriteLine("Starting two workers - each will grab one available session…");
var workerA = WorkerLoop("worker-1", client);
var workerB = WorkerLoop("worker-2", client);
await Task.WhenAll(workerA, workerB);

static async Task WorkerLoop(string name, ServiceBusClient client)
{
    var session = await client.AcceptNextSessionAsync("sessioned");
    Console.WriteLine($"[{name}] locked session '{session.SessionId}'");

    while (true)
    {
        var msg = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        if (msg is null) break;
        Console.WriteLine($"[{name}] {msg.MessageId} → \"{msg.Body}\"");
        await session.CompleteMessageAsync(msg);
    }

    Console.WriteLine($"[{name}] no more messages - releasing session.");
}
