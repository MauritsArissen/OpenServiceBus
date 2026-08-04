// .NET SDK smoke test against OpenServiceBus (Microsoft.Azure.Amqp stack).
//
// Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
//   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
//   -> topic session receive -> admin create/get/roundtrip/size limit/status gate/delete detach/delete (ATOM management API)
//   -> purge (JSON management API)
// against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.
//
// The main dotnet test suite covers far more, but this smoke keeps the cross-SDK
// comparison honest: all four stacks run the exact same operations the same way.

using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

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

    // 8. topic session receive - publish with a session id, accept the session on a
    // session-enabled SUBSCRIPTION (regression guard for issue #18).
    var topicSessionId = $"dotnet-topic-session-{stamp}";
    var topicSender = client.CreateSender("smoke-topic");
    await topicSender.SendMessageAsync(new ServiceBusMessage("topic session msg") { SessionId = topicSessionId });
    await topicSender.CloseAsync();

    var topicSession = await client.AcceptSessionAsync("smoke-topic", "smoke-topic-sessions", topicSessionId);
    var topicSessionMsg = await topicSession.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    Check("topic session receive", topicSessionMsg is not null && topicSessionMsg.Body.ToString() == "topic session msg");
    if (topicSessionMsg is not null) await topicSession.CompleteMessageAsync(topicSessionMsg);
    await topicSession.CloseAsync();

    // 9-12. admin entity CRUD - the native ServiceBusAdministrationClient against the
    // ATOM management API served on the same port as AMQP. Needs SDK 7.20.1+: older
    // versions ignore UseDevelopmentEmulator endpoints and dial https://host:443.
    var adminQueue = $"smoke-admin-dotnet-{stamp}";
    var admin = new ServiceBusAdministrationClient(conn);

    var created = (await admin.CreateQueueAsync(new CreateQueueOptions(adminQueue) { MaxDeliveryCount = 7, AutoDeleteOnIdle = TimeSpan.FromMinutes(10), MaxSizeInMegabytes = 2048 })).Value;
    Check("admin create queue", created.Name == adminQueue);

    QueueProperties fetched = await admin.GetQueueAsync(adminQueue);
    Check("admin get queue", fetched.MaxDeliveryCount == 7 && fetched.AutoDeleteOnIdle == TimeSpan.FromMinutes(10) && fetched.MaxSizeInMegabytes == 2048);

    // The admin-created queue must be immediately usable on the data plane.
    var adminSender = client.CreateSender(adminQueue);
    await adminSender.SendMessageAsync(new ServiceBusMessage("admin roundtrip"));
    await adminSender.CloseAsync();
    var adminReceiver = client.CreateReceiver(adminQueue);
    var adminMsg = await adminReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    Check("admin roundtrip", adminMsg is not null && adminMsg.Body.ToString() == "admin roundtrip");
    if (adminMsg is not null) await adminReceiver.CompleteMessageAsync(adminMsg);
    await adminReceiver.CloseAsync();

    // 13. admin size limit - a 300 KB message must be rejected against the default
    // 256 KB limit the sender link advertises (issue #24).
    var oversizeBlocked = false;
    try
    {
        await client.CreateSender(adminQueue).SendMessageAsync(new ServiceBusMessage(new byte[300 * 1024]));
    }
    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageSizeExceeded)
    {
        oversizeBlocked = true;
    }
    Check("admin size limit", oversizeBlocked);

    // 14. admin status gate - SendDisabled rejects sends with MessagingEntityDisabled,
    // flipping back to Active restores them (issue #22).
    QueueProperties gate = await admin.GetQueueAsync(adminQueue);
    gate.Status = EntityStatus.SendDisabled;
    await admin.UpdateQueueAsync(gate);
    var blocked = false;
    try
    {
        await client.CreateSender(adminQueue).SendMessageAsync(new ServiceBusMessage("must not land"));
    }
    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityDisabled)
    {
        blocked = true;
    }
    gate.Status = EntityStatus.Active;
    await admin.UpdateQueueAsync(gate);
    var reopenedSender = client.CreateSender(adminQueue);
    await reopenedSender.SendMessageAsync(new ServiceBusMessage("flowing again"));
    await reopenedSender.CloseAsync();
    Check("admin status gate", blocked);

    // 15. admin delete detach - deleting the queue under a live receiver must detach the
    // link promptly instead of stalling the close on the SDK's 60s drain timeout (issue #36).
    // Await the pending receive BEFORE closing: it settles once the broker detaches the
    // link, so the close never crosses the in-flight detach on the wire - closing during
    // the detach/retry window can strand the SDK's drain waiter for its full try-timeout.
    var doomedReceiver = client.CreateReceiver(adminQueue);
    var leftover = await doomedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (leftover is not null) await doomedReceiver.CompleteMessageAsync(leftover);
    var pendingReceive = doomedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30));
    await Task.Delay(500);
    var detachTimer = System.Diagnostics.Stopwatch.StartNew();
    await admin.DeleteQueueAsync(adminQueue);
    try { await pendingReceive; } catch (ServiceBusException) { }
    await doomedReceiver.CloseAsync();
    detachTimer.Stop();
    Check("admin delete detach", detachTimer.Elapsed < TimeSpan.FromSeconds(15), $"{detachTimer.ElapsedMilliseconds}ms");

    Check("admin delete queue", !(await admin.QueueExistsAsync(adminQueue)).Value);

    // 16. purge - the emulator-native message purge on the JSON management API
    // (issue #36): every message goes, the queue itself stays.
    var mgmtBase = Environment.GetEnvironmentVariable("SMOKE_MANAGEMENT") ?? "http://localhost:5300";
    var purgeQueue = $"smoke-purge-dotnet-{stamp}";
    await admin.CreateQueueAsync(purgeQueue);
    var purgeSender = client.CreateSender(purgeQueue);
    await purgeSender.SendMessageAsync(new ServiceBusMessage("one"));
    await purgeSender.SendMessageAsync(new ServiceBusMessage("two"));
    await purgeSender.CloseAsync();
    using var mgmtHttp = new HttpClient();
    var purgeResp = await mgmtHttp.DeleteAsync($"{mgmtBase}/queues/{purgeQueue}/messages");
    var purgeBody = await purgeResp.Content.ReadAsStringAsync();
    var purgeReceiver = client.CreateReceiver(purgeQueue);
    var afterPurge = await purgeReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
    await purgeReceiver.CloseAsync();
    Check("admin purge", purgeResp.IsSuccessStatusCode && purgeBody.Contains("\"purged\":2") && afterPurge is null,
        purgeResp.IsSuccessStatusCode ? "" : $"status {(int)purgeResp.StatusCode}");
    await admin.DeleteQueueAsync(purgeQueue);
}
catch (Exception e)
{
    Check("EXCEPTION", false, e.Message);
}

Console.WriteLine(string.Join(Environment.NewLine, results));
var failed = results.Any(r => r.StartsWith("FAIL", StringComparison.Ordinal));
Console.WriteLine(failed ? "DOTNET SMOKE: FAILED" : "DOTNET SMOKE: ALL PASS");
return failed ? 1 : 0;
