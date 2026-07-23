// Node.js SDK smoke test against OpenServiceBus.
//
// The Node SDK rides on rhea, whose AMQP frame shapes differ from the .NET SDK's
// (empty reply-link target addresses, reply-to == link name, ...). This exercises the
// full surface a real app touches: CBS auth, send, peek/schedule/cancel (all
// $management ops), receive/complete, and session receive. Regression guard for
// GitHub issue #1.
//
// Expects a running broker with the entities from ../config.json. Override the
// connection string via SMOKE_CONNECTION.

import { ServiceBusClient } from "@azure/service-bus";

const conn =
  process.env.SMOKE_CONNECTION ??
  "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

const client = new ServiceBusClient(conn);
const results = [];
const check = (name, ok, extra = "") => {
  results.push(`${ok ? "PASS" : "FAIL"} ${name}${extra ? " " + extra : ""}`);
};

const run = async () => {
  const stamp = Date.now().toString(36);

  // 1. Send - exercises $cbs put-token, the path that used to dead-end for rhea.
  const sender = client.createSender("smoke-queue");
  await sender.sendMessages({ body: "hello from node", messageId: `node-${stamp}` });
  check("send", true);

  // 2. Peek - $management via rhea.
  const receiver = client.createReceiver("smoke-queue");
  const peeked = await receiver.peekMessages(10);
  check("peek", peeked.some((m) => m.messageId === `node-${stamp}`), `(${peeked.length} peeked)`);

  // 3. Receive + complete.
  const msgs = await receiver.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  check("receive", msgs.length === 1 && msgs[0].body === "hello from node");
  await receiver.completeMessage(msgs[0]);
  check("complete", true);
  await receiver.close();

  // 4. Schedule + cancel - two more $management operations.
  const seq = await sender.scheduleMessages({ body: "future" }, new Date(Date.now() + 300_000));
  check("schedule", seq.length === 1);
  await sender.cancelScheduledMessages(seq);
  check("cancelSchedule", true);
  await sender.close();

  // 5. Sessions: send with a session id, accept the session, receive in it.
  const sessionId = `node-session-${stamp}`;
  const psender = client.createSender("smoke-sessions");
  await psender.sendMessages({ body: "session msg", sessionId });
  await psender.close();

  const session = await client.acceptSession("smoke-sessions", sessionId);
  const smsgs = await session.receiveMessages(1, { maxWaitTimeInMs: 10_000 });
  check("session receive", smsgs.length === 1 && smsgs[0].body === "session msg");
  await session.completeMessage(smsgs[0]);
  await session.close();
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
