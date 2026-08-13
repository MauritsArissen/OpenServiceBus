// Node.js SDK smoke test against OpenServiceBus (rhea-based AMQP stack).
//
// Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
//   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
//   -> topic session receive -> admin create/get/roundtrip/size limit/status gate/delete detach/delete (ATOM management API)
//   -> purge (JSON management API) -> transfer dlq -> sql filter -> dlq resend
//   -> queue dedup -> topic dedup -> batch dedup -> abandon props -> lock lost
// against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.
// Regression guard for GitHub issue #1 (rhea reply links with empty target addresses).

import { ServiceBusClient } from "@azure/service-bus";
import Long from "long";

const conn =
  process.env.SMOKE_CONNECTION ??
  "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

// Fail fast: default operation timeouts turn any broker regression into minutes of hang.
const client = new ServiceBusClient(conn, {
  retryOptions: { timeoutInMs: 15_000, maxRetries: 1 },
});
const results = [];
const check = (name, ok, extra = "") => {
  results.push(`${ok ? "PASS" : "FAIL"} ${name}${extra ? " " + extra : ""}`);
};

const run = async () => {
  const stamp = Date.now().toString(36);
  const messageId = `node-${stamp}`;

  // 1. send - exercises $cbs put-token.
  const sender = client.createSender("smoke-queue");
  await sender.sendMessages({ body: "hello from node", messageId });
  check("send", true);

  // 2. peek - $management request/response.
  const receiver = client.createReceiver("smoke-queue");
  const peeked = await receiver.peekMessages(10);
  check("peek", peeked.some((m) => m.messageId === messageId));

  // 3. receive + 4. complete.
  const msgs = await receiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  check("receive", msgs.length === 1 && msgs[0].body === "hello from node");
  await receiver.completeMessage(msgs[0]);
  check("complete", true);
  await receiver.close();

  // 5. schedule + 6. cancelSchedule - two more $management operations.
  const seq = await sender.scheduleMessages({ body: "future" }, new Date(Date.now() + 300_000));
  check("schedule", seq.length === 1);
  await sender.cancelScheduledMessages(seq);
  check("cancelSchedule", true);
  await sender.close();

  // 7. session receive - send into a session, accept it, receive in it.
  const sessionId = `node-session-${stamp}`;
  const sessionSender = client.createSender("smoke-sessions");
  await sessionSender.sendMessages({ body: "session msg", sessionId });
  await sessionSender.close();

  const session = await client.acceptSession("smoke-sessions", sessionId);
  const smsgs = await session.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  check("session receive", smsgs.length === 1 && smsgs[0].body === "session msg");
  await session.completeMessage(smsgs[0]);
  await session.close();

  // 8. topic session receive - publish with a session id, accept the session on a
  // session-enabled SUBSCRIPTION (regression guard for issue #18).
  const topicSessionId = `node-topic-session-${stamp}`;
  const topicSender = client.createSender("smoke-topic");
  await topicSender.sendMessages({ body: "topic session msg", sessionId: topicSessionId, messageId: `nts-${stamp}` });
  await topicSender.close();

  const topicSession = await client.acceptSession("smoke-topic", "smoke-topic-sessions", topicSessionId);
  const tsMsgs = await topicSession.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  check("topic session receive", tsMsgs.length === 1 && tsMsgs[0].body === "topic session msg");
  if (tsMsgs.length === 1) await topicSession.completeMessage(tsMsgs[0]);
  await topicSession.close();

  // 9-12. admin entity CRUD over the ATOM management API served on the same port as
  // AMQP. The JS SDK's ServiceBusAdministrationClient cannot target a plaintext
  // emulator yet - @azure/service-bus 7.9.5 hardcodes the scheme
  // (serviceBusAtomManagementClient.js: `https://${this.endpoint}/${path}`) - so this
  // exercises the exact same protocol with plain HTTP until the SDK catches up.
  const httpBase = `http://${conn.match(/Endpoint=sb:\/\/([^;/]+)/i)[1]}`;
  const adminQueue = `smoke-admin-node-${stamp}`;
  const queueXml =
    '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">' +
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    "<MaxDeliveryCount>7</MaxDeliveryCount><AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle><MaxSizeInMegabytes>2048</MaxSizeInMegabytes></QueueDescription></content></entry>";

  const put = await fetch(`${httpBase}/${adminQueue}?api-version=2021-05`, {
    method: "PUT",
    headers: { "Content-Type": "application/atom+xml" },
    body: queueXml,
  });
  check("admin create queue", put.status === 201, put.status === 201 ? "" : `status ${put.status}`);

  const got = await fetch(`${httpBase}/${adminQueue}?api-version=2021-05`);
  const gotBody = await got.text();
  check("admin get queue", got.status === 200 && gotBody.includes("<MaxDeliveryCount>7</MaxDeliveryCount>") && gotBody.includes("<AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle>") && gotBody.includes("<MaxSizeInMegabytes>2048</MaxSizeInMegabytes>"));

  // The admin-created queue must be immediately usable on the data plane.
  const adminSender = client.createSender(adminQueue);
  // messageId is required: the JS SDK keys its auto-lock-renewal bookkeeping on it and
  // completeMessage throws on a message that has none.
  await adminSender.sendMessages({ body: "admin roundtrip", messageId: `node-admin-${stamp}` });
  await adminSender.close();
  const adminReceiver = client.createReceiver(adminQueue);
  const aMsgs = await adminReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  check("admin roundtrip", aMsgs.length === 1 && aMsgs[0].body === "admin roundtrip");
  if (aMsgs.length === 1) await adminReceiver.completeMessage(aMsgs[0]);
  await adminReceiver.close();

  // 13. admin size limit - a 300 KB message must be rejected against the default
  // 256 KB limit the sender link advertises (issue #24).
  let oversizeBlocked = false;
  try {
    await client
      .createSender(adminQueue)
      .sendMessages({ body: new Uint8Array(300 * 1024), messageId: `oversize-${stamp}` });
  } catch {
    oversizeBlocked = true;
  }
  check("admin size limit", oversizeBlocked);

  // 14. admin status gate - SendDisabled rejects sends, Active restores them (issue #22).
  const statusXml = (status) =>
    '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">' +
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    `<MaxDeliveryCount>7</MaxDeliveryCount><Status>${status}</Status></QueueDescription></content></entry>`;
  const putStatus = (status) =>
    fetch(`${httpBase}/${adminQueue}?api-version=2021-05`, {
      method: "PUT",
      headers: { "Content-Type": "application/atom+xml", "If-Match": "*" },
      body: statusXml(status),
    });
  await putStatus("SendDisabled");
  let blocked = false;
  try {
    await client.createSender(adminQueue).sendMessages({ body: "must not land", messageId: `blocked-${stamp}` });
  } catch {
    blocked = true;
  }
  await putStatus("Active");
  const reopened = client.createSender(adminQueue);
  await reopened.sendMessages({ body: "flowing again", messageId: `reopened-${stamp}` });
  await reopened.close();
  check("admin status gate", blocked);

  // 15. admin delete detach - deleting the queue under a live receiver must detach the
  // link promptly instead of stalling the close on the SDK's drain timeout (issue #36).
  // Await the pending receive BEFORE closing: it settles once the broker detaches the
  // link, so the close never crosses the in-flight detach on the wire.
  const doomedReceiver = client.createReceiver(adminQueue);
  const leftover = await doomedReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  if (leftover.length === 1) await doomedReceiver.completeMessage(leftover[0]);
  const pendingReceive = doomedReceiver
    .receiveMessages(1, { maxWaitTimeInMs: 30_000 })
    .catch(() => []);
  await new Promise((resolve) => setTimeout(resolve, 500));
  const detachStart = Date.now();
  const del = await fetch(`${httpBase}/${adminQueue}?api-version=2021-05`, { method: "DELETE" });
  await pendingReceive;
  await doomedReceiver.close();
  const detachMs = Date.now() - detachStart;
  check("admin delete detach", detachMs < 15_000, `${detachMs}ms`);

  const gone = await fetch(`${httpBase}/${adminQueue}?api-version=2021-05`);
  check("admin delete queue", del.status === 200 && gone.status === 404);

  // 16. purge - the emulator-native message purge on the JSON management API
  // (issue #36): every message goes, the queue itself stays.
  const mgmtBase = process.env.SMOKE_MANAGEMENT ?? "http://localhost:5300";
  const purgeQueue = `smoke-purge-node-${stamp}`;
  const emptyQueueXml =
    '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">' +
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/></content></entry>';
  await fetch(`${httpBase}/${purgeQueue}?api-version=2021-05`, {
    method: "PUT",
    headers: { "Content-Type": "application/atom+xml" },
    body: emptyQueueXml,
  });
  const purgeSender = client.createSender(purgeQueue);
  await purgeSender.sendMessages({ body: "one", messageId: `purge-1-${stamp}` });
  await purgeSender.sendMessages({ body: "two", messageId: `purge-2-${stamp}` });
  await purgeSender.close();
  const purgeResp = await fetch(`${mgmtBase}/queues/${purgeQueue}/messages`, { method: "DELETE" });
  const purgeJson = purgeResp.ok ? await purgeResp.json() : {};
  const purgeReceiver = client.createReceiver(purgeQueue);
  const afterPurge = await purgeReceiver.receiveMessages(1, { maxWaitTimeInMs: 2_000 });
  await purgeReceiver.close();
  check("admin purge", purgeResp.ok && purgeJson.purged === 2 && afterPurge.length === 0,
    purgeResp.ok ? "" : `status ${purgeResp.status}`);
  await fetch(`${httpBase}/${purgeQueue}?api-version=2021-05`, { method: "DELETE" });

  // 17. transfer dlq - a send whose auto-forward target was deleted lands in the
  // source queue's $Transfer/$DeadLetterQueue with a descriptive reason (issue #25).
  const fwdTarget = `smoke-fwd-target-${stamp}`;
  const fwdSource = `smoke-fwd-${stamp}`;
  await fetch(`${httpBase}/${fwdTarget}?api-version=2021-05`, {
    method: "PUT",
    headers: { "Content-Type": "application/atom+xml" },
    body: emptyQueueXml,
  });
  const fwdXml =
    '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">' +
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    `<ForwardTo>${fwdTarget}</ForwardTo></QueueDescription></content></entry>`;
  await fetch(`${httpBase}/${fwdSource}?api-version=2021-05`, {
    method: "PUT",
    headers: { "Content-Type": "application/atom+xml" },
    body: fwdXml,
  });
  await fetch(`${httpBase}/${fwdTarget}?api-version=2021-05`, { method: "DELETE" });
  const fwdSender = client.createSender(fwdSource);
  await fwdSender.sendMessages({ body: "undeliverable", messageId: `tdlq-${stamp}` });
  await fwdSender.close();
  const transferReceiver = client.createReceiver(fwdSource, { subQueueType: "transferDeadLetter" });
  const movedMsgs = await transferReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  if (movedMsgs.length === 1) await transferReceiver.completeMessage(movedMsgs[0]);
  await transferReceiver.close();
  const transferOk = movedMsgs.length === 1 && movedMsgs[0].deadLetterReason === "MessagingEntityNotFound";
  check(
    "transfer dlq",
    transferOk,
    transferOk ? "" : movedMsgs.length === 0 ? "no message" : movedMsgs[0].deadLetterReason ?? "no reason",
  );
  await fetch(`${httpBase}/${fwdSource}?api-version=2021-05`, { method: "DELETE" });

  // 18. sql filter - an arithmetic SQL rule routes only matching messages (issue #26).
  const filterTopic = `smoke-filter-${stamp}`;
  const atomPut = (path, descriptionXml) =>
    fetch(`${httpBase}/${path}?api-version=2021-05`, {
      method: "PUT",
      headers: { "Content-Type": "application/atom+xml" },
      body:
        '<entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">' +
        descriptionXml +
        "</content></entry>",
    });
  await atomPut(filterTopic,
    '<TopicDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>');
  await atomPut(`${filterTopic}/subscriptions/flt`,
    '<SubscriptionDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>');
  await fetch(`${httpBase}/${filterTopic}/subscriptions/flt/rules/$Default?api-version=2021-05`, { method: "DELETE" });
  const ruleResp = await atomPut(`${filterTopic}/subscriptions/flt/rules/high`,
    '<RuleDescription xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    '<Filter i:type="SqlFilter"><SqlExpression>priority + 1 >= @threshold</SqlExpression>' +
    '<Parameters><KeyValueOfstringanyType><Key>@threshold</Key>' +
    '<Value xmlns:d6p1="http://www.w3.org/2001/XMLSchema" i:type="d6p1:int">5</Value>' +
    '</KeyValueOfstringanyType></Parameters></Filter></RuleDescription>');
  const filterSender = client.createSender(filterTopic);
  await filterSender.sendMessages({ body: "match", messageId: `flt-match-${stamp}`, applicationProperties: { priority: 4 } });
  await filterSender.sendMessages({ body: "miss", messageId: `flt-miss-${stamp}`, applicationProperties: { priority: 1 } });
  await filterSender.close();
  const filterReceiver = client.createReceiver(filterTopic, "flt");
  const filtered = await filterReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  if (filtered.length === 1) await filterReceiver.completeMessage(filtered[0]);
  const extra = await filterReceiver.receiveMessages(1, { maxWaitTimeInMs: 2_000 });
  await filterReceiver.close();
  const filterOk =
    ruleResp.status === 201 &&
    filtered.length === 1 &&
    filtered[0].messageId === `flt-match-${stamp}` &&
    extra.length === 0;
  check(
    "sql filter",
    filterOk,
    filterOk ? "" : `rule ${ruleResp.status}, got ${filtered.length} msg(s), extra ${extra.length}`,
  );
  await fetch(`${httpBase}/${filterTopic}?api-version=2021-05`, { method: "DELETE" });

  // 19. dlq resend - the peek-clone-send recipe the Explorer's Resend rides on (issue
  // #28): dead-letter a message, peek it off the DLQ, send a cleaned copy with a fresh
  // id, receive the copy on the queue, and verify the DLQ original stays untouched.
  const resendQueue = `smoke-resend-${stamp}`;
  await atomPut(resendQueue,
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>');
  const resendSender = client.createSender(resendQueue);
  await resendSender.sendMessages({
    body: "resend me",
    messageId: `resend-orig-${stamp}`,
    applicationProperties: { attempt: 1 },
  });
  const resendReceiver = client.createReceiver(resendQueue);
  const poison = await resendReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  if (poison.length === 1) {
    await resendReceiver.deadLetterMessage(poison[0], { deadLetterReason: "smoke-poison" });
  }
  const resendDlq = client.createReceiver(resendQueue, { subQueueType: "deadLetter" });
  const dead = (await resendDlq.peekMessages(10, { fromSequenceNumber: Long.fromNumber(1) })).find(
    (m) => m.messageId === `resend-orig-${stamp}`,
  );
  let resendOk = false;
  let resendDetail = "original not peekable from the DLQ";
  if (dead) {
    const props = { ...dead.applicationProperties };
    delete props.DeadLetterReason;
    delete props.DeadLetterErrorDescription;
    delete props["Diagnostic-Id"];
    await resendSender.sendMessages({
      body: dead.body,
      messageId: `resend-copy-${stamp}`,
      applicationProperties: props,
    });
    const redelivered = await resendReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
    if (redelivered.length === 1) await resendReceiver.completeMessage(redelivered[0]);
    const stillDead = (await resendDlq.peekMessages(10, { fromSequenceNumber: Long.fromNumber(1) })).some(
      (m) => m.messageId === `resend-orig-${stamp}`,
    );
    resendOk =
      redelivered.length === 1 &&
      redelivered[0].messageId === `resend-copy-${stamp}` &&
      redelivered[0].body === "resend me" &&
      (redelivered[0].deliveryCount ?? 1) <= 1 &&
      !("DeadLetterReason" in (redelivered[0].applicationProperties ?? {})) &&
      stillDead;
    resendDetail = redelivered.length === 0 ? "copy not received" : stillDead ? "" : "DLQ original vanished";
  }
  check("dlq resend", resendOk, resendOk ? "" : resendDetail);
  await resendSender.close();
  await resendReceiver.close();
  await resendDlq.close();
  await fetch(`${httpBase}/${resendQueue}?api-version=2021-05`, { method: "DELETE" });

  // 20. queue dedup - a duplicate-detection queue silently drops a repeated MessageId
  // within the window (issue #29 hardening): three sends, one shared id, two survivors.
  const dedupQueue = `smoke-dedup-${stamp}`;
  await atomPut(dedupQueue,
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    "<RequiresDuplicateDetection>true</RequiresDuplicateDetection>" +
    "<DuplicateDetectionHistoryTimeWindow>PT10M</DuplicateDetectionHistoryTimeWindow></QueueDescription>");
  const dedupSender = client.createSender(dedupQueue);
  await dedupSender.sendMessages({ body: "first", messageId: `dupq-${stamp}` });
  await dedupSender.sendMessages({ body: "retry", messageId: `dupq-${stamp}` });
  await dedupSender.sendMessages({ body: "unique", messageId: `dupq-other-${stamp}` });
  const dedupReceiver = client.createReceiver(dedupQueue);
  let dedupSeen = 0;
  for (;;) {
    const got = await dedupReceiver.receiveMessages(1, { maxWaitTimeInMs: 2000 });
    if (got.length === 0) break;
    await dedupReceiver.completeMessage(got[0]);
    dedupSeen++;
  }
  check("queue dedup", dedupSeen === 2, dedupSeen === 2 ? "" : `got ${dedupSeen} message(s)`);

  // 21. topic dedup - duplicate detection runs at the TOPIC before fan-out (issue #29):
  // a duplicate publish reaches zero subscriptions; each subscription sees exactly one copy.
  const dedupTopic = `smoke-dedup-topic-${stamp}`;
  await atomPut(dedupTopic,
    '<TopicDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    "<RequiresDuplicateDetection>true</RequiresDuplicateDetection></TopicDescription>");
  await atomPut(`${dedupTopic}/subscriptions/s1`,
    '<SubscriptionDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>');
  await atomPut(`${dedupTopic}/subscriptions/s2`,
    '<SubscriptionDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>');
  const topicDedupSender = client.createSender(dedupTopic);
  await topicDedupSender.sendMessages({ body: "evt", messageId: `dupt-${stamp}` });
  await topicDedupSender.sendMessages({ body: "evt-retry", messageId: `dupt-${stamp}` });
  await topicDedupSender.close();
  let topicDedupOk = true;
  let topicDedupDetail = "";
  for (const sub of ["s1", "s2"]) {
    const subReceiver = client.createReceiver(dedupTopic, sub);
    let got = 0;
    for (;;) {
      const batch = await subReceiver.receiveMessages(1, { maxWaitTimeInMs: 2000 });
      if (batch.length === 0) break;
      await subReceiver.completeMessage(batch[0]);
      got++;
    }
    await subReceiver.close();
    if (got !== 1) {
      topicDedupOk = false;
      topicDedupDetail = `subscription ${sub} got ${got} message(s)`;
    }
  }
  check("topic dedup", topicDedupOk, topicDedupDetail);
  await fetch(`${httpBase}/${dedupTopic}?api-version=2021-05`, { method: "DELETE" });

  // 22. batch dedup - one batched envelope with an in-batch duplicate: each inner
  // message is checked individually, so the duplicate is dropped and distinct ids land.
  await dedupSender.sendMessages([
    { body: "one", messageId: `dupb-${stamp}` },
    { body: "dup-of-one", messageId: `dupb-${stamp}` },
    { body: "two", messageId: `dupb2-${stamp}` },
  ]);
  let batchSeen = 0;
  for (;;) {
    const got = await dedupReceiver.receiveMessages(1, { maxWaitTimeInMs: 2000 });
    if (got.length === 0) break;
    await dedupReceiver.completeMessage(got[0]);
    batchSeen++;
  }
  check("batch dedup", batchSeen === 2, batchSeen === 2 ? "" : `got ${batchSeen} message(s)`);
  await dedupSender.close();
  await dedupReceiver.close();
  await fetch(`${httpBase}/${dedupQueue}?api-version=2021-05`, { method: "DELETE" });

  // 23. abandon props - PropertiesToModify on abandon (issue #30): the map merges into
  // the stored message so the redelivery carries it.
  const ptmQueue = `smoke-ptm-${stamp}`;
  await atomPut(ptmQueue,
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect"/>');
  const ptmSender = client.createSender(ptmQueue);
  await ptmSender.sendMessages({
    body: "try me",
    messageId: `ptm-${stamp}`,
    applicationProperties: { existing: "old" },
  });
  const ptmReceiver = client.createReceiver(ptmQueue);
  const [ptmFirst] = await ptmReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  if (ptmFirst) {
    await ptmReceiver.abandonMessage(ptmFirst, { "retry-reason": "timeout", existing: "new" });
  }
  const [ptmSecond] = await ptmReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  if (ptmSecond) await ptmReceiver.completeMessage(ptmSecond);
  const ptmOk =
    !!ptmSecond &&
    ptmSecond.applicationProperties?.["retry-reason"] === "timeout" &&
    ptmSecond.applicationProperties?.existing === "new" &&
    ptmSecond.deliveryCount === 1;
  check("abandon props", ptmOk, ptmOk ? "" : !ptmSecond ? "no redelivery" : "properties missing");
  await ptmSender.close();
  await ptmReceiver.close();
  await fetch(`${httpBase}/${ptmQueue}?api-version=2021-05`, { method: "DELETE" });

  // 24. lock lost - settling after the lock deadline fails with MessageLockLost instead
  // of silently succeeding, and the message redelivers (issue #52).
  const llQueue = `smoke-locklost-${stamp}`;
  await atomPut(llQueue,
    '<QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">' +
    "<LockDuration>PT5S</LockDuration></QueueDescription>");
  const llSender = client.createSender(llQueue);
  await llSender.sendMessages({ body: "expire me", messageId: `ll-${stamp}` });
  // The JS receiver auto-renews message locks for up to 5 minutes by default, which
  // would keep the lock alive through the sleep - disable it so expiry actually happens.
  const llReceiver = client.createReceiver(llQueue, { maxAutoLockRenewalDurationInMs: 0 });
  const [llMsg] = await llReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  let llOk = false;
  let llDetail = "no message";
  if (llMsg) {
    await new Promise((resolve) => setTimeout(resolve, 7000));
    try {
      await llReceiver.completeMessage(llMsg);
      llDetail = "complete unexpectedly succeeded";
    } catch (e) {
      llOk = e?.code === "MessageLockLost";
      llDetail = llOk ? "" : `${e?.code ?? e?.name}: ${e?.message}`;
    }
    const [llRedelivered] = await llReceiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
    if (llRedelivered) await llReceiver.completeMessage(llRedelivered);
    if (llOk && !llRedelivered) {
      llOk = false;
      llDetail = "no redelivery";
    }
  }
  check("lock lost", llOk, llDetail);
  await llSender.close();
  await llReceiver.close();
  await fetch(`${httpBase}/${llQueue}?api-version=2021-05`, { method: "DELETE" });
};

const timeout = new Promise((_, rej) =>
  setTimeout(() => rej(new Error("smoke test timed out after 90s")), 90_000));

try {
  await Promise.race([run(), timeout]);
} catch (e) {
  check("EXCEPTION", false, e?.message ?? String(e));
}
await client.close().catch(() => {});

console.log(results.join("\n"));
const failed = results.some((r) => r.startsWith("FAIL"));
console.log(failed ? "NODE SMOKE: FAILED" : "NODE SMOKE: ALL PASS");
process.exit(failed ? 1 : 0);
