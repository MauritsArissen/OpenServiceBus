// .NET SDK smoke test against OpenServiceBus (Microsoft.Azure.Amqp stack).
//
// Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
//   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
// against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.
//
// The main dotnet test suite covers far more, but this smoke keeps the cross-SDK
// comparison honest: all four stacks run the exact same operations the same way.

using Azure.Messaging.ServiceBus;

var conn = Environment.GetEnvironmentVariable("SMOKE_CONNECTION")
    ?? "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

var results = new List<string>();
void Check(string name, bool ok, string extra = "") =>
    results.Add($"{(ok ? "PASS" : "FAIL")} {name}{(extra.Length > 0 ? " " + extra : "")}");

await using var client = new ServiceBusClient(conn, new ServiceBusClientOptions
{
    // Fail fast: the SDK's default 60s try-timeout turns a broker regression into a
    // minutes-long hang; 15s is plenty against a local broker.
    RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(15), MaxRetries = 1 },
});

try
{
    var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x");
    var messageId = $"dotnet-{stamp}";

    // 1. send - exercises $cbs put-token.
    var sender = client.CreateSender("smoke-queue");
    await sender.SendMessageAsync(new ServiceBusMessage("hello from dotnet") { MessageId = messageId });
    Check("send", true);

    // 2. peek - $management request/response.
    var receiver = client.CreateReceiver("smoke-queue");
    var peeked = await receiver.PeekMessagesAsync(10);
    Check("peek", peeked.Any(m => m.MessageId == messageId));

    // 3. receive + 4. complete.
    var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    Check("receive", msg is not null && msg.Body.ToString() == "hello from dotnet");
    if (msg is not null) await receiver.CompleteMessageAsync(msg);
    Check("complete", msg is not null);
    await receiver.CloseAsync();

    // 5. schedule + 6. cancelSchedule - two more $management operations.
    var sequenceNumber = await sender.ScheduleMessageAsync(
        new ServiceBusMessage("future"), DateTimeOffset.UtcNow.AddMinutes(5));
    Check("schedule", sequenceNumber > 0);
    await sender.CancelScheduledMessageAsync(sequenceNumber);
    Check("cancelSchedule", true);
    await sender.CloseAsync();

    // 7. session receive - send into a session, accept it, receive in it.
    var sessionId = $"dotnet-session-{stamp}";
    var sessionSender = client.CreateSender("smoke-sessions");
    await sessionSender.SendMessageAsync(new ServiceBusMessage("session msg") { SessionId = sessionId });
    await sessionSender.CloseAsync();

    var session = await client.AcceptSessionAsync("smoke-sessions", sessionId);
    var sessionMsg = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    Check("session receive", sessionMsg is not null && sessionMsg.Body.ToString() == "session msg");
    if (sessionMsg is not null) await session.CompleteMessageAsync(sessionMsg);
    await session.CloseAsync();
}
catch (Exception e)
{
    Check("EXCEPTION", false, e.Message);
}

Console.WriteLine(string.Join(Environment.NewLine, results));
var failed = results.Any(r => r.StartsWith("FAIL", StringComparison.Ordinal));
Console.WriteLine(failed ? "DOTNET SMOKE: FAILED" : "DOTNET SMOKE: ALL PASS");
return failed ? 1 : 0;
