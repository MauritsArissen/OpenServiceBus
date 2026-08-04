package io.openservicebus.smoke;

import com.azure.core.amqp.AmqpRetryOptions;
import com.azure.messaging.servicebus.ServiceBusClientBuilder;
import com.azure.messaging.servicebus.ServiceBusMessage;
import com.azure.messaging.servicebus.ServiceBusReceivedMessage;
import com.azure.messaging.servicebus.ServiceBusReceiverClient;
import com.azure.messaging.servicebus.ServiceBusSenderClient;
import com.azure.messaging.servicebus.ServiceBusSessionReceiverClient;

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
        } catch (Exception e) {
            results.add("FAIL admin: " + e);
        }

        results.forEach(System.out::println);
        boolean failed = results.stream().anyMatch(r -> r.startsWith("FAIL"));
        System.out.println(failed ? "JAVA SMOKE: FAILED" : "JAVA SMOKE: ALL PASS");
        System.exit(failed ? 1 : 0);
    }
}
