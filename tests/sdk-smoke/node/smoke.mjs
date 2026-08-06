// Node.js SDK smoke test against OpenServiceBus (rhea-based AMQP stack).
//
// Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
//   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
//   -> topic session receive -> admin create/get/roundtrip/size limit/status gate/delete detach/delete (ATOM management API)
//   -> purge (JSON management API) -> transfer dlq -> sql filter
// against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.
// Regression guard for GitHub issue #1 (rhea reply links with empty target addresses).

import { ServiceBusClient } from "@azure/service-bus";

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
};

const timeout = new Promise((_, rej) =>
  setTimeout(() => rej(new Error("smoke test timed out after 60s")), 60_000));

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
