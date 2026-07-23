using Azure.Messaging.ServiceBus;

// Minimal QuickStart: send a message, receive it back, complete it. Talks to an
// OpenServiceBus broker reachable at the configured endpoint (sb://localhost:5672 by
// default - bring it up via `docker compose up` in this folder).

var connectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION")
    ?? "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

const string queueName = "quickstart";

await using var client = new ServiceBusClient(connectionString);

Console.WriteLine($"Sending three messages to '{queueName}'…");
var sender = client.CreateSender(queueName);
for (var i = 1; i <= 3; i++)
{
    await sender.SendMessageAsync(new ServiceBusMessage($"hello #{i}")
    {
        MessageId = $"qs-{i:000}",
    });
    Console.WriteLine($"  sent qs-{i:000}");
}

Console.WriteLine();
Console.WriteLine($"Receiving from '{queueName}'…");
var receiver = client.CreateReceiver(queueName);
for (var i = 0; i < 3; i++)
{
    var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
    if (msg is null) { Console.WriteLine("  (timeout)"); break; }

    Console.WriteLine($"  got id={msg.MessageId} body=\"{msg.Body}\" seq={msg.SequenceNumber}");
    await receiver.CompleteMessageAsync(msg);
}

Console.WriteLine();
Console.WriteLine("Done. Try `docker compose down -v` to wipe the broker volume.");
