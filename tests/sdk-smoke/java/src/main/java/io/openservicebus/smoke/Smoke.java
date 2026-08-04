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
 *   -> topic session receive -> admin create/get/roundtrip/status gate/delete (ATOM management API)
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
                + "<MaxDeliveryCount>7</MaxDeliveryCount><AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle></QueueDescription></content></entry>";
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
            results.add(got.statusCode() == 200 && got.body().contains("<MaxDeliveryCount>7</MaxDeliveryCount>") && got.body().contains("<AutoDeleteOnIdle>PT10M</AutoDeleteOnIdle>")
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

            // 13. admin status gate - SendDisabled rejects sends, Active restores them (issue #22).
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

            HttpResponse<String> del = http.send(
                HttpRequest.newBuilder(queueUri).DELETE().build(), HttpResponse.BodyHandlers.ofString());
            HttpResponse<String> gone = http.send(
                HttpRequest.newBuilder(queueUri).GET().build(), HttpResponse.BodyHandlers.ofString());
            results.add(del.statusCode() == 200 && gone.statusCode() == 404
                ? "PASS admin delete queue"
                : "FAIL admin delete queue: delete " + del.statusCode() + ", get-after " + gone.statusCode());
        } catch (Exception e) {
            results.add("FAIL admin: " + e);
        }

        results.forEach(System.out::println);
        boolean failed = results.stream().anyMatch(r -> r.startsWith("FAIL"));
        System.out.println(failed ? "JAVA SMOKE: FAILED" : "JAVA SMOKE: ALL PASS");
        System.exit(failed ? 1 : 0);
    }
}
