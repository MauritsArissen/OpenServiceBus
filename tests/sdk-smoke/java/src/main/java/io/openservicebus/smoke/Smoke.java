package io.openservicebus.smoke;

import com.azure.core.amqp.AmqpRetryOptions;
import com.azure.messaging.servicebus.ServiceBusClientBuilder;
import com.azure.messaging.servicebus.ServiceBusMessage;
import com.azure.messaging.servicebus.ServiceBusReceivedMessage;
import com.azure.messaging.servicebus.ServiceBusReceiverClient;
import com.azure.messaging.servicebus.ServiceBusSenderClient;
import com.azure.messaging.servicebus.ServiceBusSessionReceiverClient;
import com.azure.messaging.servicebus.models.AbandonOptions;
import com.azure.messaging.servicebus.models.DeadLetterOptions;
import com.azure.messaging.servicebus.models.SubQueue;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.time.OffsetDateTime;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Java SDK smoke test against OpenServiceBus (proton-j AMQP stack).
 *
 * Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
 *   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
 *   -> topic session receive -> admin create/get/roundtrip/size limit/status gate/delete detach/delete (ATOM management API)
 *   -> purge (JSON management API) -> transfer dlq -> sql filter -> dlq resend
 *   -> queue dedup -> topic dedup -> batch dedup -> abandon props
 * against the entities in ../config.json. Override the broker via SMOKE_CONNECTION.
 * Regression guard for GitHub issue #1 (proton-j sends ulong message-ids).
 *
 * Exit code 0 = all pass; 1 = at least one failure.
 */
public final class Smoke {

    private Smoke() { }

    public static void main(String[] args) {
        String conn = System.getenv().getOrDefault("SMOKE_CONNECTION",
            "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;"
                + "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true");

        // Fail fast: the SDK's default operation timeout is ~4 minutes PER operation,
        // which turns any broker regression into a 15+ minute hang.
        AmqpRetryOptions retry = new AmqpRetryOptions()
            .setTryTimeout(Duration.ofSeconds(15))
            .setMaxRetries(1);

        List<String> results = new ArrayList<>();
        String stamp = Long.toString(System.currentTimeMillis(), 36);
        String messageId = "java-" + stamp;

        try (ServiceBusSenderClient sender = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .sender().queueName("smoke-queue").buildClient()) {

            // 1. send - exercises $cbs put-token.
            ServiceBusMessage message = new ServiceBusMessage("hello from java");
            message.setMessageId(messageId);
            sender.sendMessage(message);
            results.add("PASS send");

            try (ServiceBusReceiverClient receiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName("smoke-queue").disableAutoComplete().buildClient()) {

                // 2. peek - $management request/response.
                ServiceBusReceivedMessage peeked = receiver.peekMessage();
                results.add(peeked != null && messageId.equals(peeked.getMessageId())
                    ? "PASS peek"
                    : "FAIL peek: got " + (peeked == null ? "null" : peeked.getMessageId()));

                // 3. receive + 4. complete.
                boolean received = false;
                for (ServiceBusReceivedMessage m : receiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    received = "hello from java".equals(m.getBody().toString());
                    receiver.complete(m);
                }
                results.add(received ? "PASS receive" : "FAIL receive: no message within 10s");
                results.add(received ? "PASS complete" : "FAIL complete: nothing to complete");
            }

            // 5. schedule + 6. cancelSchedule - two more $management operations.
            Long sequenceNumber = sender.scheduleMessage(
                new ServiceBusMessage("future"), OffsetDateTime.now().plusMinutes(5));
            results.add("PASS schedule");
            sender.cancelScheduledMessage(sequenceNumber);
            results.add("PASS cancelSchedule");
        } catch (Exception e) {
            results.add("FAIL EXCEPTION " + e);
        }

        // 7. session receive - send into a session, accept it, receive in it.
        String sessionId = "java-session-" + stamp;
        try (ServiceBusSenderClient sessionSender = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .sender().queueName("smoke-sessions").buildClient()) {

            ServiceBusMessage sessionMessage = new ServiceBusMessage("session msg");
            sessionMessage.setSessionId(sessionId);
            sessionSender.sendMessage(sessionMessage);

            try (ServiceBusSessionReceiverClient sessionClient = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sessionReceiver().queueName("smoke-sessions").disableAutoComplete().buildClient();
                 ServiceBusReceiverClient session = sessionClient.acceptSession(sessionId)) {

                boolean received = false;
                for (ServiceBusReceivedMessage m : session.receiveMessages(1, Duration.ofSeconds(10))) {
                    received = "session msg".equals(m.getBody().toString());
                    session.complete(m);
                }
                results.add(received ? "PASS session receive" : "FAIL session receive: no message within 10s");
            }
        } catch (Exception e) {
            results.add("FAIL session receive: " + e);
        }

        // 8. topic session receive - publish with a session id, accept the session on a
        // session-enabled SUBSCRIPTION (regression guard for issue #18).
        String topicSessionId = "java-topic-session-" + stamp;
        try (ServiceBusSenderClient topicSender = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .sender().topicName("smoke-topic").buildClient()) {

            ServiceBusMessage topicMessage = new ServiceBusMessage("topic session msg");
            topicMessage.setSessionId(topicSessionId);
            topicSender.sendMessage(topicMessage);

            try (ServiceBusSessionReceiverClient topicSessionClient = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sessionReceiver().topicName("smoke-topic").subscriptionName("smoke-topic-sessions")
                    .disableAutoComplete().buildClient();
                 ServiceBusReceiverClient topicSession = topicSessionClient.acceptSession(topicSessionId)) {

                boolean received = false;
                for (ServiceBusReceivedMessage m : topicSession.receiveMessages(1, Duration.ofSeconds(10))) {
                    received = "topic session msg".equals(m.getBody().toString());
                    topicSession.complete(m);
                }
                results.add(received ? "PASS topic session receive" : "FAIL topic session receive: no message within 10s");
            }
        } catch (Exception e) {
            results.add("FAIL topic session receive: " + e);
        }

        // 9-12. admin entity CRUD over the ATOM management API served on the same port as
        // AMQP. The Java SDK's ServiceBusAdministrationClient cannot target a plaintext
        // emulator yet - verified against 7.17.11: it dials https://host:443 even with an
        // explicit http endpoint() override - so this exercises the exact same protocol
        // with plain HTTP until the SDK catches up.
        try {
            Matcher endpoint = Pattern.compile("Endpoint=sb://([^;/]+)", Pattern.CASE_INSENSITIVE).matcher(conn);
            if (!endpoint.find()) {
                throw new IllegalStateException("no sb:// endpoint in connection string");
            }
            String base = "http://" + endpoint.group(1);
            String adminQueue = "smoke-admin-java-" + stamp;
            String queueXml =
                "<entry xmlns=\"http://www.w3.org/2005/Atom\"><content type=\"application/xml\">"
                + "<QueueDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">"
                + "<MaxDeliveryCount>7</MaxDeliveryCount><AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle><MaxSizeInMegabytes>2048</MaxSizeInMegabytes></QueueDescription></content></entry>";
            HttpClient http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).build();
            URI queueUri = URI.create(base + "/" + adminQueue + "?api-version=2021-05");

            HttpResponse<String> put = http.send(
                HttpRequest.newBuilder(queueUri)
                    .header("Content-Type", "application/atom+xml")
                    .PUT(HttpRequest.BodyPublishers.ofString(queueXml)).build(),
                HttpResponse.BodyHandlers.ofString());
            results.add(put.statusCode() == 201
                ? "PASS admin create queue"
                : "FAIL admin create queue: status " + put.statusCode());

            HttpResponse<String> got = http.send(
                HttpRequest.newBuilder(queueUri).GET().build(), HttpResponse.BodyHandlers.ofString());
            results.add(got.statusCode() == 200 && got.body().contains("<MaxDeliveryCount>7</MaxDeliveryCount>") && got.body().contains("<AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle>") && got.body().contains("<MaxSizeInMegabytes>2048</MaxSizeInMegabytes>")
                ? "PASS admin get queue"
                : "FAIL admin get queue: status " + got.statusCode());

            // The admin-created queue must be immediately usable on the data plane.
            boolean roundTripped = false;
            try (ServiceBusSenderClient adminSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(adminQueue).buildClient();
                 ServiceBusReceiverClient adminReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(adminQueue).disableAutoComplete().buildClient()) {
                adminSender.sendMessage(new ServiceBusMessage("admin roundtrip"));
                for (ServiceBusReceivedMessage m : adminReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    roundTripped = "admin roundtrip".equals(m.getBody().toString());
                    adminReceiver.complete(m);
                }
            }
            results.add(roundTripped ? "PASS admin roundtrip" : "FAIL admin roundtrip: no message within 10s");

            // 13. admin size limit - a 300 KB message must be rejected against the default
            // 256 KB limit the sender link advertises (issue #24).
            boolean oversizeBlocked = false;
            try (ServiceBusSenderClient oversizeSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(adminQueue).buildClient()) {
                oversizeSender.sendMessage(new ServiceBusMessage(new byte[300 * 1024]));
            } catch (Exception e) {
                oversizeBlocked = true;
            }
            results.add(oversizeBlocked ? "PASS admin size limit" : "FAIL admin size limit: 300 KB send succeeded");

            // 14. admin status gate - SendDisabled rejects sends, Active restores them (issue #22).
            String statusXmlTemplate =
                "<entry xmlns=\"http://www.w3.org/2005/Atom\"><content type=\"application/xml\">"
                + "<QueueDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">"
                + "<MaxDeliveryCount>7</MaxDeliveryCount><Status>%s</Status></QueueDescription></content></entry>";
            http.send(
                HttpRequest.newBuilder(queueUri)
                    .header("Content-Type", "application/atom+xml").header("If-Match", "*")
                    .PUT(HttpRequest.BodyPublishers.ofString(String.format(statusXmlTemplate, "SendDisabled"))).build(),
                HttpResponse.BodyHandlers.ofString());
            boolean blocked = false;
            try (ServiceBusSenderClient blockedSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(adminQueue).buildClient()) {
                blockedSender.sendMessage(new ServiceBusMessage("must not land"));
            } catch (Exception e) {
                blocked = true;
            }
            http.send(
                HttpRequest.newBuilder(queueUri)
                    .header("Content-Type", "application/atom+xml").header("If-Match", "*")
                    .PUT(HttpRequest.BodyPublishers.ofString(String.format(statusXmlTemplate, "Active"))).build(),
                HttpResponse.BodyHandlers.ofString());
            try (ServiceBusSenderClient reopenedSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(adminQueue).buildClient()) {
                reopenedSender.sendMessage(new ServiceBusMessage("flowing again"));
            }
            results.add(blocked ? "PASS admin status gate" : "FAIL admin status gate: send on SendDisabled queue succeeded");

            // 15. admin delete detach - deleting the queue while a receiver is live must
            // not stall that receiver's close on a drain timeout (issue #36). The delete
            // fires from a helper thread while the main thread waits in receiveMessages;
            // the sync Java client sits out its own maxWaitTime after the detach, so the
            // prompt-close assertion is on close() itself.
            long closeElapsedMs = -1;
            final int[] deleteStatus = {0};
            ServiceBusReceiverClient doomedReceiver = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .receiver().queueName(adminQueue).disableAutoComplete().buildClient();
            try {
                for (ServiceBusReceivedMessage m : doomedReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    doomedReceiver.complete(m);
                }
                Thread deleter = new Thread(() -> {
                    try {
                        Thread.sleep(500);
                        deleteStatus[0] = http.send(
                            HttpRequest.newBuilder(queueUri).DELETE().build(),
                            HttpResponse.BodyHandlers.ofString()).statusCode();
                    } catch (Exception ignored) {
                    }
                });
                deleter.start();
                try {
                    for (ServiceBusReceivedMessage m : doomedReceiver.receiveMessages(1, Duration.ofSeconds(5))) {
                        // deleted entity - nothing is expected here
                    }
                } catch (Exception ignored) {
                }
                deleter.join();
                long started = System.nanoTime();
                doomedReceiver.close();
                closeElapsedMs = (System.nanoTime() - started) / 1_000_000;
            } catch (Exception e) {
                results.add("FAIL admin delete detach: " + e);
            } finally {
                try {
                    doomedReceiver.close();
                } catch (Exception ignored) {
                }
            }
            if (closeElapsedMs >= 0) {
                results.add(closeElapsedMs < 10_000
                    ? "PASS admin delete detach"
                    : "FAIL admin delete detach: close took " + closeElapsedMs + "ms");
            }

            HttpResponse<String> gone = http.send(
                HttpRequest.newBuilder(queueUri).GET().build(), HttpResponse.BodyHandlers.ofString());
            results.add(deleteStatus[0] == 200 && gone.statusCode() == 404
                ? "PASS admin delete queue"
                : "FAIL admin delete queue: delete " + deleteStatus[0] + ", get-after " + gone.statusCode());

            // 16. purge - the emulator-native message purge on the JSON management API
            // (issue #36): every message goes, the queue itself stays.
            String mgmtBase = System.getenv().getOrDefault("SMOKE_MANAGEMENT", "http://localhost:5300");
            String purgeQueue = "smoke-purge-java-" + stamp;
            String emptyQueueXml =
                "<entry xmlns=\"http://www.w3.org/2005/Atom\"><content type=\"application/xml\">"
                + "<QueueDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\"/></content></entry>";
            URI purgeQueueUri = URI.create(base + "/" + purgeQueue + "?api-version=2021-05");
            http.send(
                HttpRequest.newBuilder(purgeQueueUri)
                    .header("Content-Type", "application/atom+xml")
                    .PUT(HttpRequest.BodyPublishers.ofString(emptyQueueXml)).build(),
                HttpResponse.BodyHandlers.ofString());
            try (ServiceBusSenderClient purgeSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(purgeQueue).buildClient()) {
                purgeSender.sendMessage(new ServiceBusMessage("one"));
                purgeSender.sendMessage(new ServiceBusMessage("two"));
            }
            HttpResponse<String> purgeResp = http.send(
                HttpRequest.newBuilder(URI.create(mgmtBase + "/queues/" + purgeQueue + "/messages"))
                    .DELETE().build(),
                HttpResponse.BodyHandlers.ofString());
            boolean afterPurgeEmpty = true;
            try (ServiceBusReceiverClient purgeReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(purgeQueue).disableAutoComplete().buildClient()) {
                for (ServiceBusReceivedMessage m : purgeReceiver.receiveMessages(1, Duration.ofSeconds(2))) {
                    afterPurgeEmpty = false;
                }
            }
            results.add(purgeResp.statusCode() == 200
                    && purgeResp.body().replace(" ", "").contains("\"purged\":2")
                    && afterPurgeEmpty
                ? "PASS admin purge"
                : "FAIL admin purge: status " + purgeResp.statusCode() + ", body " + purgeResp.body());
            http.send(
                HttpRequest.newBuilder(purgeQueueUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());

            // 17. transfer dlq - a send whose auto-forward target was deleted lands in the
            // source queue's $Transfer/$DeadLetterQueue with a descriptive reason (issue #25).
            String fwdTarget = "smoke-fwd-target-" + stamp;
            String fwdSource = "smoke-fwd-" + stamp;
            URI fwdTargetUri = URI.create(base + "/" + fwdTarget + "?api-version=2021-05");
            URI fwdSourceUri = URI.create(base + "/" + fwdSource + "?api-version=2021-05");
            http.send(
                HttpRequest.newBuilder(fwdTargetUri)
                    .header("Content-Type", "application/atom+xml")
                    .PUT(HttpRequest.BodyPublishers.ofString(emptyQueueXml)).build(),
                HttpResponse.BodyHandlers.ofString());
            String fwdXml =
                "<entry xmlns=\"http://www.w3.org/2005/Atom\"><content type=\"application/xml\">"
                + "<QueueDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\">"
                + "<ForwardTo>" + fwdTarget + "</ForwardTo></QueueDescription></content></entry>";
            http.send(
                HttpRequest.newBuilder(fwdSourceUri)
                    .header("Content-Type", "application/atom+xml")
                    .PUT(HttpRequest.BodyPublishers.ofString(fwdXml)).build(),
                HttpResponse.BodyHandlers.ofString());
            http.send(
                HttpRequest.newBuilder(fwdTargetUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());
            try (ServiceBusSenderClient fwdSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(fwdSource).buildClient()) {
                fwdSender.sendMessage(new ServiceBusMessage("undeliverable"));
            }
            String movedReason = null;
            try (ServiceBusReceiverClient transferReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(fwdSource).subQueue(SubQueue.TRANSFER_DEAD_LETTER_QUEUE)
                    .disableAutoComplete().buildClient()) {
                for (ServiceBusReceivedMessage m : transferReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    movedReason = m.getDeadLetterReason();
                    transferReceiver.complete(m);
                }
            }
            results.add("MessagingEntityNotFound".equals(movedReason)
                ? "PASS transfer dlq"
                : "FAIL transfer dlq: reason " + movedReason);
            http.send(
                HttpRequest.newBuilder(fwdSourceUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());

            // 18. sql filter - an arithmetic SQL rule routes only matching messages (issue #26).
            String filterTopic = "smoke-filter-" + stamp;
            String sbNs = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";
            java.util.function.BiFunction<String, String, HttpRequest> atomPut = (path, descriptionXml) ->
                HttpRequest.newBuilder(URI.create(base + "/" + path + "?api-version=2021-05"))
                    .header("Content-Type", "application/atom+xml")
                    .PUT(HttpRequest.BodyPublishers.ofString(
                        "<entry xmlns=\"http://www.w3.org/2005/Atom\"><content type=\"application/xml\">"
                        + descriptionXml + "</content></entry>"))
                    .build();
            http.send(atomPut.apply(filterTopic,
                "<TopicDescription xmlns=\"" + sbNs + "\"/>"), HttpResponse.BodyHandlers.ofString());
            http.send(atomPut.apply(filterTopic + "/subscriptions/flt",
                "<SubscriptionDescription xmlns=\"" + sbNs + "\"/>"), HttpResponse.BodyHandlers.ofString());
            http.send(
                HttpRequest.newBuilder(URI.create(base + "/" + filterTopic + "/subscriptions/flt/rules/$Default?api-version=2021-05"))
                    .DELETE().build(),
                HttpResponse.BodyHandlers.ofString());
            HttpResponse<String> ruleResp = http.send(atomPut.apply(filterTopic + "/subscriptions/flt/rules/high",
                "<RuleDescription xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"" + sbNs + "\">"
                + "<Filter i:type=\"SqlFilter\"><SqlExpression>priority + 1 &gt;= @threshold</SqlExpression>"
                + "<Parameters><KeyValueOfstringanyType><Key>@threshold</Key>"
                + "<Value xmlns:d6p1=\"http://www.w3.org/2001/XMLSchema\" i:type=\"d6p1:int\">5</Value>"
                + "</KeyValueOfstringanyType></Parameters></Filter></RuleDescription>"),
                HttpResponse.BodyHandlers.ofString());
            try (ServiceBusSenderClient filterSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().topicName(filterTopic).buildClient()) {
                ServiceBusMessage match = new ServiceBusMessage("match");
                match.setMessageId("flt-match-" + stamp);
                match.getApplicationProperties().put("priority", 4);
                filterSender.sendMessage(match);
                ServiceBusMessage miss = new ServiceBusMessage("miss");
                miss.setMessageId("flt-miss-" + stamp);
                miss.getApplicationProperties().put("priority", 1);
                filterSender.sendMessage(miss);
            }
            String filteredId = null;
            int filteredCount = 0;
            try (ServiceBusReceiverClient filterReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().topicName(filterTopic).subscriptionName("flt")
                    .disableAutoComplete().buildClient()) {
                for (ServiceBusReceivedMessage m : filterReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    filteredId = m.getMessageId();
                    filteredCount++;
                    filterReceiver.complete(m);
                }
                for (ServiceBusReceivedMessage m : filterReceiver.receiveMessages(1, Duration.ofSeconds(2))) {
                    filteredCount++;
                    filterReceiver.complete(m);
                }
            }
            results.add(ruleResp.statusCode() == 201 && filteredCount == 1 && ("flt-match-" + stamp).equals(filteredId)
                ? "PASS sql filter"
                : "FAIL sql filter: rule " + ruleResp.statusCode() + ", got " + filteredCount + " msg(s), id " + filteredId);
            http.send(
                HttpRequest.newBuilder(URI.create(base + "/" + filterTopic + "?api-version=2021-05")).DELETE().build(),
                HttpResponse.BodyHandlers.ofString());

            // 19. dlq resend - the peek-clone-send recipe the Explorer's Resend rides on
            // (issue #28): dead-letter a message, peek it off the DLQ, send a cleaned copy
            // with a fresh id, receive the copy, and verify the DLQ original stays untouched.
            String resendQueue = "smoke-resend-" + stamp;
            URI resendQueueUri = URI.create(base + "/" + resendQueue + "?api-version=2021-05");
            http.send(
                HttpRequest.newBuilder(resendQueueUri)
                    .header("Content-Type", "application/atom+xml")
                    .PUT(HttpRequest.BodyPublishers.ofString(emptyQueueXml)).build(),
                HttpResponse.BodyHandlers.ofString());
            try (ServiceBusSenderClient resendSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(resendQueue).buildClient();
                 ServiceBusReceiverClient resendReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(resendQueue).disableAutoComplete().buildClient();
                 ServiceBusReceiverClient resendDlq = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(resendQueue).subQueue(SubQueue.DEAD_LETTER_QUEUE)
                    .disableAutoComplete().buildClient()) {
                ServiceBusMessage poison = new ServiceBusMessage("resend me");
                poison.setMessageId("resend-orig-" + stamp);
                poison.getApplicationProperties().put("attempt", 1);
                resendSender.sendMessage(poison);
                for (ServiceBusReceivedMessage m : resendReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    resendReceiver.deadLetter(m, new DeadLetterOptions().setDeadLetterReason("smoke-poison"));
                }
                ServiceBusReceivedMessage dead = null;
                for (ServiceBusReceivedMessage m : resendDlq.peekMessages(10, 1)) {
                    if (("resend-orig-" + stamp).equals(m.getMessageId())) {
                        dead = m;
                    }
                }
                boolean resendOk = false;
                String resendFail = "original not peekable from the DLQ";
                if (dead != null) {
                    ServiceBusMessage resendCopy = new ServiceBusMessage(dead.getBody());
                    resendCopy.setMessageId("resend-copy-" + stamp);
                    dead.getApplicationProperties().forEach((k, v) -> {
                        if (!"DeadLetterReason".equals(k) && !"DeadLetterErrorDescription".equals(k)
                                && !"Diagnostic-Id".equals(k)) {
                            resendCopy.getApplicationProperties().put(k, v);
                        }
                    });
                    resendSender.sendMessage(resendCopy);
                    ServiceBusReceivedMessage redelivered = null;
                    for (ServiceBusReceivedMessage m : resendReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                        redelivered = m;
                        resendReceiver.complete(m);
                    }
                    boolean stillDead = false;
                    for (ServiceBusReceivedMessage m : resendDlq.peekMessages(10, 1)) {
                        if (("resend-orig-" + stamp).equals(m.getMessageId())) {
                            stillDead = true;
                        }
                    }
                    resendOk = redelivered != null
                        && ("resend-copy-" + stamp).equals(redelivered.getMessageId())
                        && "resend me".equals(redelivered.getBody().toString())
                        && !redelivered.getApplicationProperties().containsKey("DeadLetterReason")
                        && stillDead;
                    resendFail = redelivered == null ? "copy not received"
                        : stillDead ? "copy mismatch" : "DLQ original vanished";
                }
                results.add(resendOk ? "PASS dlq resend" : "FAIL dlq resend: " + resendFail);
            }
            http.send(
                HttpRequest.newBuilder(resendQueueUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());

            // 20. queue dedup - a duplicate-detection queue silently drops a repeated
            // MessageId within the window (issue #29 hardening): three sends, one shared
            // id, two survivors.
            String dedupQueue = "smoke-dedup-" + stamp;
            URI dedupQueueUri = URI.create(base + "/" + dedupQueue + "?api-version=2021-05");
            http.send(atomPut.apply(dedupQueue,
                "<QueueDescription xmlns=\"" + sbNs + "\">"
                + "<RequiresDuplicateDetection>true</RequiresDuplicateDetection>"
                + "<DuplicateDetectionHistoryTimeWindow>PT10M</DuplicateDetectionHistoryTimeWindow>"
                + "</QueueDescription>"), HttpResponse.BodyHandlers.ofString());
            int dedupSeen = 0;
            try (ServiceBusSenderClient dedupSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(dedupQueue).buildClient();
                 ServiceBusReceiverClient dedupReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(dedupQueue).disableAutoComplete().buildClient()) {
                ServiceBusMessage firstSend = new ServiceBusMessage("first");
                firstSend.setMessageId("dupq-" + stamp);
                dedupSender.sendMessage(firstSend);
                ServiceBusMessage retrySend = new ServiceBusMessage("retry");
                retrySend.setMessageId("dupq-" + stamp);
                dedupSender.sendMessage(retrySend);
                ServiceBusMessage uniqueSend = new ServiceBusMessage("unique");
                uniqueSend.setMessageId("dupq-other-" + stamp);
                dedupSender.sendMessage(uniqueSend);
                for (ServiceBusReceivedMessage m : dedupReceiver.receiveMessages(5, Duration.ofSeconds(2))) {
                    dedupReceiver.complete(m);
                    dedupSeen++;
                }
            }
            results.add(dedupSeen == 2
                ? "PASS queue dedup"
                : "FAIL queue dedup: got " + dedupSeen + " message(s)");

            // 21. topic dedup - duplicate detection runs at the TOPIC before fan-out
            // (issue #29): a duplicate publish reaches zero subscriptions; each
            // subscription sees exactly one copy.
            String dedupTopic = "smoke-dedup-topic-" + stamp;
            http.send(atomPut.apply(dedupTopic,
                "<TopicDescription xmlns=\"" + sbNs + "\">"
                + "<RequiresDuplicateDetection>true</RequiresDuplicateDetection></TopicDescription>"),
                HttpResponse.BodyHandlers.ofString());
            http.send(atomPut.apply(dedupTopic + "/subscriptions/s1",
                "<SubscriptionDescription xmlns=\"" + sbNs + "\"/>"), HttpResponse.BodyHandlers.ofString());
            http.send(atomPut.apply(dedupTopic + "/subscriptions/s2",
                "<SubscriptionDescription xmlns=\"" + sbNs + "\"/>"), HttpResponse.BodyHandlers.ofString());
            try (ServiceBusSenderClient topicDedupSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().topicName(dedupTopic).buildClient()) {
                ServiceBusMessage evt = new ServiceBusMessage("evt");
                evt.setMessageId("dupt-" + stamp);
                topicDedupSender.sendMessage(evt);
                ServiceBusMessage evtRetry = new ServiceBusMessage("evt-retry");
                evtRetry.setMessageId("dupt-" + stamp);
                topicDedupSender.sendMessage(evtRetry);
            }
            boolean topicDedupOk = true;
            String topicDedupDetail = "";
            for (String sub : new String[] { "s1", "s2" }) {
                int subSeen = 0;
                try (ServiceBusReceiverClient subReceiver = new ServiceBusClientBuilder()
                        .connectionString(conn).retryOptions(retry)
                        .receiver().topicName(dedupTopic).subscriptionName(sub)
                        .disableAutoComplete().buildClient()) {
                    for (ServiceBusReceivedMessage m : subReceiver.receiveMessages(5, Duration.ofSeconds(2))) {
                        subReceiver.complete(m);
                        subSeen++;
                    }
                }
                if (subSeen != 1) {
                    topicDedupOk = false;
                    topicDedupDetail = "subscription " + sub + " got " + subSeen + " message(s)";
                }
            }
            results.add(topicDedupOk ? "PASS topic dedup" : "FAIL topic dedup: " + topicDedupDetail);
            http.send(
                HttpRequest.newBuilder(URI.create(base + "/" + dedupTopic + "?api-version=2021-05")).DELETE().build(),
                HttpResponse.BodyHandlers.ofString());

            // 22. batch dedup - one batched envelope with an in-batch duplicate: each
            // inner message is checked individually, so the duplicate is dropped and
            // distinct ids land.
            int batchSeen = 0;
            try (ServiceBusSenderClient batchSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(dedupQueue).buildClient();
                 ServiceBusReceiverClient batchReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(dedupQueue).disableAutoComplete().buildClient()) {
                ServiceBusMessage one = new ServiceBusMessage("one");
                one.setMessageId("dupb-" + stamp);
                ServiceBusMessage dupOfOne = new ServiceBusMessage("dup-of-one");
                dupOfOne.setMessageId("dupb-" + stamp);
                ServiceBusMessage two = new ServiceBusMessage("two");
                two.setMessageId("dupb2-" + stamp);
                batchSender.sendMessages(java.util.List.of(one, dupOfOne, two));
                for (ServiceBusReceivedMessage m : batchReceiver.receiveMessages(5, Duration.ofSeconds(2))) {
                    batchReceiver.complete(m);
                    batchSeen++;
                }
            }
            results.add(batchSeen == 2
                ? "PASS batch dedup"
                : "FAIL batch dedup: got " + batchSeen + " message(s)");
            http.send(
                HttpRequest.newBuilder(dedupQueueUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());

            // 23. abandon props - PropertiesToModify on abandon (issue #30): the map merges
            // into the stored message so the redelivery carries it.
            String ptmQueue = "smoke-ptm-" + stamp;
            URI ptmQueueUri = URI.create(base + "/" + ptmQueue + "?api-version=2021-05");
            http.send(atomPut.apply(ptmQueue,
                "<QueueDescription xmlns=\"" + sbNs + "\"/>"), HttpResponse.BodyHandlers.ofString());
            boolean ptmOk = false;
            String ptmDetail = "no redelivery";
            try (ServiceBusSenderClient ptmSender = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .sender().queueName(ptmQueue).buildClient();
                 ServiceBusReceiverClient ptmReceiver = new ServiceBusClientBuilder()
                    .connectionString(conn).retryOptions(retry)
                    .receiver().queueName(ptmQueue).disableAutoComplete().buildClient()) {
                ServiceBusMessage tryMe = new ServiceBusMessage("try me");
                tryMe.setMessageId("ptm-" + stamp);
                tryMe.getApplicationProperties().put("existing", "old");
                ptmSender.sendMessage(tryMe);
                for (ServiceBusReceivedMessage m : ptmReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    java.util.Map<String, Object> mods = new java.util.HashMap<>();
                    mods.put("retry-reason", "timeout");
                    mods.put("existing", "new");
                    ptmReceiver.abandon(m, new AbandonOptions().setPropertiesToModify(mods));
                }
                for (ServiceBusReceivedMessage m : ptmReceiver.receiveMessages(1, Duration.ofSeconds(10))) {
                    ptmOk = "timeout".equals(m.getApplicationProperties().get("retry-reason"))
                        && "new".equals(m.getApplicationProperties().get("existing"));
                    ptmDetail = ptmOk ? "" : "properties missing";
                    ptmReceiver.complete(m);
                }
            }
            results.add(ptmOk ? "PASS abandon props" : "FAIL abandon props: " + ptmDetail);
            http.send(
                HttpRequest.newBuilder(ptmQueueUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());
        } catch (Exception e) {
            results.add("FAIL admin: " + e);
        }

        results.forEach(System.out::println);
        boolean failed = results.stream().anyMatch(r -> r.startsWith("FAIL"));
        System.out.println(failed ? "JAVA SMOKE: FAILED" : "JAVA SMOKE: ALL PASS");
        System.exit(failed ? 1 : 0);
    }
}
