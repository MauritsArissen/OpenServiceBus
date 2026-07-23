// Node.js SDK smoke test against OpenServiceBus (rhea-based AMQP stack).
//
// Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
//   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
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
