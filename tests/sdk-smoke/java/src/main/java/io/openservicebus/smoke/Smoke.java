package io.openservicebus.smoke;

import com.azure.core.amqp.AmqpRetryOptions;
import com.azure.messaging.servicebus.ServiceBusClientBuilder;
import com.azure.messaging.servicebus.ServiceBusMessage;
import com.azure.messaging.servicebus.ServiceBusReceivedMessage;
import com.azure.messaging.servicebus.ServiceBusReceiverClient;
import com.azure.messaging.servicebus.ServiceBusSenderClient;
import com.azure.messaging.servicebus.ServiceBusSessionReceiverClient;

import java.time.Duration;
import java.time.OffsetDateTime;
import java.util.ArrayList;
import java.util.List;

/**
 * Java SDK smoke test against OpenServiceBus (proton-j AMQP stack).
 *
 * Canonical smoke sequence - identical across the dotnet/node/java/python smokes:
 *   send -> peek -> receive -> complete -> schedule -> cancelSchedule -> session receive
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

        results.forEach(System.out::println);
        boolean failed = results.stream().anyMatch(r -> r.startsWith("FAIL"));
        System.out.println(failed ? "JAVA SMOKE: FAILED" : "JAVA SMOKE: ALL PASS");
        System.exit(failed ? 1 : 0);
    }
}
