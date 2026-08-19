// .NET SDK smoke test against OpenServiceBus (Microsoft.Azure.Amqp stack).
//
// Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
//   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
//   -> topic session receive -> admin create/get/roundtrip/size limit/status gate/delete detach/delete (ATOM management API)
//   -> purge (JSON management API) -> transfer dlq -> sql filter -> dlq resend
//   -> queue dedup -> topic dedup -> batch dedup -> abandon props -> lock lost
//   -> topic schedule -> system props
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

    // 17. transfer dlq - a send whose auto-forward target was deleted lands in the
    // source queue's $Transfer/$DeadLetterQueue with a descriptive reason (issue #25).
    var fwdTarget = $"smoke-fwd-target-{stamp}";
    var fwdSource = $"smoke-fwd-{stamp}";
    await admin.CreateQueueAsync(fwdTarget);
    await admin.CreateQueueAsync(new CreateQueueOptions(fwdSource) { ForwardTo = fwdTarget });
    await admin.DeleteQueueAsync(fwdTarget);
    var fwdSender = client.CreateSender(fwdSource);
    await fwdSender.SendMessageAsync(new ServiceBusMessage("undeliverable") { MessageId = $"tdlq-{stamp}" });
    await fwdSender.CloseAsync();
    var transferReceiver = client.CreateReceiver(fwdSource,
        new ServiceBusReceiverOptions { SubQueue = SubQueue.TransferDeadLetter });
    var movedMsg = await transferReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (movedMsg is not null) await transferReceiver.CompleteMessageAsync(movedMsg);
    await transferReceiver.CloseAsync();
    var transferOk = movedMsg is not null && movedMsg.DeadLetterReason == "MessagingEntityNotFound";
    Check("transfer dlq", transferOk,
        transferOk ? "" : movedMsg is null ? "no message" : movedMsg.DeadLetterReason ?? "no reason");
    await admin.DeleteQueueAsync(fwdSource);

    // 18. sql filter - an arithmetic SQL rule routes only matching messages (issue #26).
    var filterTopic = $"smoke-filter-{stamp}";
    await admin.CreateTopicAsync(filterTopic);
    await admin.CreateSubscriptionAsync(filterTopic, "flt");
    await admin.DeleteRuleAsync(filterTopic, "flt", "$Default");
    var filterRule = new SqlRuleFilter("priority + 1 >= @threshold");
    filterRule.Parameters.Add("@threshold", 5);
    await admin.CreateRuleAsync(filterTopic, "flt", new CreateRuleOptions("high", filterRule));
    var filterSender = client.CreateSender(filterTopic);
    var matchMsg = new ServiceBusMessage("match") { MessageId = $"flt-match-{stamp}" };
    matchMsg.ApplicationProperties["priority"] = 4;
    var missMsg = new ServiceBusMessage("miss") { MessageId = $"flt-miss-{stamp}" };
    missMsg.ApplicationProperties["priority"] = 1;
    await filterSender.SendMessageAsync(matchMsg);
    await filterSender.SendMessageAsync(missMsg);
    await filterSender.CloseAsync();
    var filterReceiver = client.CreateReceiver(filterTopic, "flt");
    var filtered = await filterReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (filtered is not null) await filterReceiver.CompleteMessageAsync(filtered);
    var extra = await filterReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
    await filterReceiver.CloseAsync();
    var filterOk = filtered?.MessageId == $"flt-match-{stamp}" && extra is null;
    Check("sql filter", filterOk,
        filterOk ? "" : filtered is null ? "no message" : extra is null ? filtered.MessageId! : "non-matching message leaked");
    await admin.DeleteTopicAsync(filterTopic);

    // 19. dlq resend - the peek-clone-send recipe the Explorer's Resend rides on (issue
    // #28): dead-letter a message, peek it off the DLQ, send a cleaned copy with a fresh
    // id, receive the copy on the queue, and verify the DLQ original stays untouched.
    var resendQueue = $"smoke-resend-{stamp}";
    await admin.CreateQueueAsync(resendQueue);
    var resendSender = client.CreateSender(resendQueue);
    var poison = new ServiceBusMessage("resend me") { MessageId = $"resend-orig-{stamp}" };
    poison.ApplicationProperties["attempt"] = 1;
    await resendSender.SendMessageAsync(poison);
    var resendReceiver = client.CreateReceiver(resendQueue);
    var poisonMsg = await resendReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (poisonMsg is not null) await resendReceiver.DeadLetterMessageAsync(poisonMsg, "smoke-poison");
    var resendDlq = client.CreateReceiver(resendQueue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    var dead = (await resendDlq.PeekMessagesAsync(10, fromSequenceNumber: 1)).FirstOrDefault(m => m.MessageId == $"resend-orig-{stamp}");
    var resendOk = false;
    var resendDetail = "original not peekable from the DLQ";
    if (dead is not null)
    {
        var resendCopy = new ServiceBusMessage(dead.Body) { MessageId = $"resend-copy-{stamp}" };
        foreach (var kv in dead.ApplicationProperties)
        {
            if (kv.Key is "DeadLetterReason" or "DeadLetterErrorDescription" or "Diagnostic-Id") continue;
            resendCopy.ApplicationProperties[kv.Key] = kv.Value;
        }
        await resendSender.SendMessageAsync(resendCopy);
        var redelivered = await resendReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        if (redelivered is not null) await resendReceiver.CompleteMessageAsync(redelivered);
        var stillDead = (await resendDlq.PeekMessagesAsync(10, fromSequenceNumber: 1)).Any(m => m.MessageId == $"resend-orig-{stamp}");
        resendOk = redelivered is not null
            && redelivered.MessageId == $"resend-copy-{stamp}"
            && redelivered.Body.ToString() == "resend me"
            && redelivered.DeliveryCount == 1
            && !redelivered.ApplicationProperties.ContainsKey("DeadLetterReason")
            && stillDead;
        resendDetail = redelivered is null ? "copy not received" : stillDead ? "" : "DLQ original vanished";
    }
    Check("dlq resend", resendOk, resendOk ? "" : resendDetail);
    await resendSender.CloseAsync();
    await resendReceiver.CloseAsync();
    await resendDlq.CloseAsync();
    await admin.DeleteQueueAsync(resendQueue);

    // 20. queue dedup - a duplicate-detection queue silently drops a repeated MessageId
    // within the window (issue #29 hardening): three sends, one shared id, two survivors.
    var dedupQueue = $"smoke-dedup-{stamp}";
    await admin.CreateQueueAsync(new CreateQueueOptions(dedupQueue)
    {
        RequiresDuplicateDetection = true,
        DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
    });
    var dedupSender = client.CreateSender(dedupQueue);
    await dedupSender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = $"dupq-{stamp}" });
    await dedupSender.SendMessageAsync(new ServiceBusMessage("retry") { MessageId = $"dupq-{stamp}" });
    await dedupSender.SendMessageAsync(new ServiceBusMessage("unique") { MessageId = $"dupq-other-{stamp}" });
    var dedupReceiver = client.CreateReceiver(dedupQueue);
    var dedupSeen = 0;
    while (await dedupReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2)) is { } dedupMsg)
    {
        await dedupReceiver.CompleteMessageAsync(dedupMsg);
        dedupSeen++;
    }
    Check("queue dedup", dedupSeen == 2, dedupSeen == 2 ? "" : $"got {dedupSeen} message(s)");

    // 21. topic dedup - duplicate detection runs at the TOPIC before fan-out (issue #29):
    // a duplicate publish reaches zero subscriptions; each subscription sees exactly one copy.
    var dedupTopic = $"smoke-dedup-topic-{stamp}";
    await admin.CreateTopicAsync(new CreateTopicOptions(dedupTopic) { RequiresDuplicateDetection = true });
    await admin.CreateSubscriptionAsync(dedupTopic, "s1");
    await admin.CreateSubscriptionAsync(dedupTopic, "s2");
    var topicDedupSender = client.CreateSender(dedupTopic);
    await topicDedupSender.SendMessageAsync(new ServiceBusMessage("evt") { MessageId = $"dupt-{stamp}" });
    await topicDedupSender.SendMessageAsync(new ServiceBusMessage("evt-retry") { MessageId = $"dupt-{stamp}" });
    await topicDedupSender.CloseAsync();
    var topicDedupOk = true;
    var topicDedupDetail = "";
    foreach (var sub in new[] { "s1", "s2" })
    {
        var subReceiver = client.CreateReceiver(dedupTopic, sub);
        var got = 0;
        while (await subReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2)) is { } subMsg)
        {
            await subReceiver.CompleteMessageAsync(subMsg);
            got++;
        }
        await subReceiver.CloseAsync();
        if (got != 1)
        {
            topicDedupOk = false;
            topicDedupDetail = $"subscription {sub} got {got} message(s)";
        }
    }
    Check("topic dedup", topicDedupOk, topicDedupDetail);
    await admin.DeleteTopicAsync(dedupTopic);

    // 22. batch dedup - one batched envelope with an in-batch duplicate: each inner
    // message is checked individually, so the duplicate is dropped and distinct ids land.
    await dedupSender.SendMessagesAsync(new List<ServiceBusMessage>
    {
        new("one") { MessageId = $"dupb-{stamp}" },
        new("dup-of-one") { MessageId = $"dupb-{stamp}" },
        new("two") { MessageId = $"dupb2-{stamp}" },
    });
    var batchSeen = 0;
    while (await dedupReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2)) is { } batchMsg)
    {
        await dedupReceiver.CompleteMessageAsync(batchMsg);
        batchSeen++;
    }
    Check("batch dedup", batchSeen == 2, batchSeen == 2 ? "" : $"got {batchSeen} message(s)");
    await dedupSender.CloseAsync();
    await dedupReceiver.CloseAsync();
    await admin.DeleteQueueAsync(dedupQueue);

    // 23. abandon props - PropertiesToModify on abandon (issue #30): the map merges into
    // the stored message so the redelivery carries it. (The Python SDK does not expose
    // propertiesToModify, so its script notes this step instead of running it.)
    var ptmQueue = $"smoke-ptm-{stamp}";
    await admin.CreateQueueAsync(ptmQueue);
    var ptmSender = client.CreateSender(ptmQueue);
    var ptmMsg = new ServiceBusMessage("try me") { MessageId = $"ptm-{stamp}" };
    ptmMsg.ApplicationProperties["existing"] = "old";
    await ptmSender.SendMessageAsync(ptmMsg);
    var ptmReceiver = client.CreateReceiver(ptmQueue);
    var ptmFirst = await ptmReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (ptmFirst is not null)
    {
        await ptmReceiver.AbandonMessageAsync(ptmFirst, new Dictionary<string, object>
        {
            ["retry-reason"] = "timeout",
            ["existing"] = "new",
        });
    }
    var ptmSecond = await ptmReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (ptmSecond is not null) await ptmReceiver.CompleteMessageAsync(ptmSecond);
    var ptmOk = ptmSecond is not null
        && Equals(ptmSecond.ApplicationProperties.GetValueOrDefault("retry-reason"), "timeout")
        && Equals(ptmSecond.ApplicationProperties.GetValueOrDefault("existing"), "new")
        && ptmSecond.DeliveryCount == 2;
    Check("abandon props", ptmOk, ptmOk ? "" : ptmSecond is null ? "no redelivery" : "properties missing");
    await ptmSender.CloseAsync();
    await ptmReceiver.CloseAsync();
    await admin.DeleteQueueAsync(ptmQueue);

    // 24. lock lost - settling after the lock deadline fails with MessageLockLost instead
    // of silently succeeding, and the message redelivers (issue #52). Short lock so the
    // smoke stays fast.
    var llQueue = $"smoke-locklost-{stamp}";
    await admin.CreateQueueAsync(new CreateQueueOptions(llQueue) { LockDuration = TimeSpan.FromSeconds(5) });
    var llSender = client.CreateSender(llQueue);
    await llSender.SendMessageAsync(new ServiceBusMessage("expire me") { MessageId = $"ll-{stamp}" });
    await llSender.CloseAsync();
    var llReceiver = client.CreateReceiver(llQueue);
    var llMsg = await llReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    var llOk = false;
    var llDetail = "no message";
    if (llMsg is not null)
    {
        await Task.Delay(TimeSpan.FromSeconds(7));
        try
        {
            await llReceiver.CompleteMessageAsync(llMsg);
            llDetail = "complete unexpectedly succeeded";
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            llOk = true;
            llDetail = "";
        }
        catch (Exception ex)
        {
            llDetail = ex.GetType().Name + ": " + ex.Message;
        }
        var llRedelivered = await llReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        if (llRedelivered is not null) await llReceiver.CompleteMessageAsync(llRedelivered);
        if (llOk && llRedelivered is null)
        {
            llOk = false;
            llDetail = "no redelivery";
        }
    }
    Check("lock lost", llOk, llDetail);
    await llReceiver.CloseAsync();
    await admin.DeleteQueueAsync(llQueue);

    // 25. topic schedule - ScheduleMessageAsync against a TOPIC sender (issue #53): the
    // broker holds the publish at the topic and fans it out at the due time; a cancelled
    // schedule never fans out.
    var schedTopic = $"smoke-sched-topic-{stamp}";
    await admin.CreateTopicAsync(schedTopic);
    await admin.CreateSubscriptionAsync(schedTopic, "s1");
    var schedSender = client.CreateSender(schedTopic);
    var dueSeq = await schedSender.ScheduleMessageAsync(
        new ServiceBusMessage("due soon") { MessageId = $"ts-{stamp}" }, DateTimeOffset.UtcNow.AddSeconds(2));
    var cancelSeq = await schedSender.ScheduleMessageAsync(
        new ServiceBusMessage("never") { MessageId = $"ts-never-{stamp}" }, DateTimeOffset.UtcNow.AddMinutes(5));
    await schedSender.CancelScheduledMessageAsync(cancelSeq);
    await schedSender.CloseAsync();
    var schedReceiver = client.CreateReceiver(schedTopic, "s1");
    var dueMsg = await schedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    if (dueMsg is not null) await schedReceiver.CompleteMessageAsync(dueMsg);
    var extraSched = await schedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
    if (extraSched is not null) await schedReceiver.CompleteMessageAsync(extraSched);
    await schedReceiver.CloseAsync();
    var schedOk = dueSeq > 0 && dueMsg is not null && dueMsg.Body.ToString() == "due soon" && extraSched is null;
    Check("topic schedule", schedOk,
        schedOk ? "" : dueMsg is null ? "scheduled message never arrived" : "cancelled message leaked");
    await admin.DeleteTopicAsync(schedTopic);

    // 26. system props - broker-stamped system properties (issue #55): a scheduled message
    // peeks as Scheduled; the delivery carries State=Active, the original scheduled enqueue
    // time, the round-tripped PartitionKey, and EnqueuedSequenceNumber; deferred retrieval
    // reports State=Deferred.
    var spQueue = $"smoke-sysprops-{stamp}";
    await admin.CreateQueueAsync(spQueue);
    var spSender = client.CreateSender(spQueue);
    var spWhen = DateTimeOffset.UtcNow.AddSeconds(2);
    var spSeq = await spSender.ScheduleMessageAsync(
        new ServiceBusMessage("sys props") { MessageId = $"sp-{stamp}", PartitionKey = $"pk-{stamp}" }, spWhen);
    var spReceiver = client.CreateReceiver(spQueue);
    var spPeeked = (await spReceiver.PeekMessagesAsync(10, fromSequenceNumber: 1)).FirstOrDefault(m => m.MessageId == $"sp-{stamp}");
    var spReceived = await spReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
    var spOk = false;
    var spDetail = "scheduled message never arrived";
    if (spReceived is not null)
    {
        await spReceiver.DeferMessageAsync(spReceived);
        var spDeferred = await spReceiver.ReceiveDeferredMessageAsync(spReceived.SequenceNumber);
        if (spDeferred is not null) await spReceiver.CompleteMessageAsync(spDeferred);
        spOk = spPeeked is not null && spPeeked.State == ServiceBusMessageState.Scheduled
            && spReceived.State == ServiceBusMessageState.Active
            && spReceived.PartitionKey == $"pk-{stamp}"
            && spReceived.EnqueuedSequenceNumber == spSeq
            && Math.Abs((spReceived.ScheduledEnqueueTime - spWhen).TotalSeconds) < 1
            && spDeferred is not null && spDeferred.State == ServiceBusMessageState.Deferred;
        spDetail = spOk ? "" :
            $"peek={spPeeked?.State.ToString() ?? "miss"} state={spReceived.State} pk={spReceived.PartitionKey ?? "<null>"}" +
            $" eseq={spReceived.EnqueuedSequenceNumber}/{spSeq} sched={spReceived.ScheduledEnqueueTime:O}" +
            $" deferred={spDeferred?.State.ToString() ?? "miss"}";
    }
    Check("system props", spOk, spDetail);
    await spSender.CloseAsync();
    await spReceiver.CloseAsync();
    await admin.DeleteQueueAsync(spQueue);
}
catch (Exception e)
{
    Check("EXCEPTION", false, e.Message);
}

Console.WriteLine(string.Join(Environment.NewLine, results));
var failed = results.Any(r => r.StartsWith("FAIL", StringComparison.Ordinal));
Console.WriteLine(failed ? "DOTNET SMOKE: FAILED" : "DOTNET SMOKE: ALL PASS");
return failed ? 1 : 0;
