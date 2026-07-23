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
 * Java SDK smoke test against OpenServiceBus. The Java SDK rides on proton-j, whose
 * AMQP behavior differs from both the .NET SDK and rhea - this exercises CBS auth,
 * send, peek / schedule / cancel (the $management operations), receive/complete, and
 * session receive against a running broker with the entities from ../config.json.
 *
 * Override the connection string via the SMOKE_CONNECTION environment variable.
 * Exit code 0 = all pass; 1 = at least one failure.
 */
public final class Smoke {

    private Smoke() { }

    public static void main(String[] args) {
        String conn = System.getenv().getOrDefault("SMOKE_CONNECTION",
            "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;"
                + "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true");

        // Fail fast: the SDK's default operation timeout is ~4 minutes PER operation,
        // which turns any broker regression into a 15+ minute hang. 15s is plenty locally.
        AmqpRetryOptions retry = new AmqpRetryOptions()
            .setTryTimeout(java.time.Duration.ofSeconds(15))
            .setMaxRetries(1);

        List<String> failures = new ArrayList<>();
        String stamp = Long.toString(System.currentTimeMillis(), 36);
        String messageId = "java-" + stamp;

        // 1. Send (exercises $cbs) + schedule/cancel (exercises $management).
        try (ServiceBusSenderClient sender = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .sender().queueName("smoke-queue").buildClient()) {

            ServiceBusMessage message = new ServiceBusMessage("hello from java");
            message.setMessageId(messageId);
            sender.sendMessage(message);
            System.out.println("PASS send");

            Long sequenceNumber = sender.scheduleMessage(
                new ServiceBusMessage("future"), OffsetDateTime.now().plusMinutes(5));
            System.out.println("PASS schedule");
            sender.cancelScheduledMessage(sequenceNumber);
            System.out.println("PASS cancelSchedule");
        } catch (Exception e) {
            failures.add("send/schedule: " + e);
        }

        // 2. Peek + receive + complete.
        try (ServiceBusReceiverClient receiver = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .receiver().queueName("smoke-queue").disableAutoComplete().buildClient()) {

            ServiceBusReceivedMessage peeked = receiver.peekMessage();
            if (peeked != null && messageId.equals(peeked.getMessageId())) {
                System.out.println("PASS peek");
            } else {
                failures.add("peek: expected " + messageId + ", got "
                    + (peeked == null ? "null" : peeked.getMessageId()));
            }

            boolean received = false;
            for (ServiceBusReceivedMessage m : receiver.receiveMessages(1, Duration.ofSeconds(10))) {
                received = true;
                receiver.complete(m);
            }
            if (received) {
                System.out.println("PASS receive/complete");
            } else {
                failures.add("receive: no message within 10s");
            }
        } catch (Exception e) {
            failures.add("receive: " + e);
        }

        // 3. Sessions: send into a session, accept it, receive in it.
        String sessionId = "java-session-" + stamp;
        try (ServiceBusSenderClient sessionSender = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .sender().queueName("smoke-sessions").buildClient()) {

            ServiceBusMessage sessionMessage = new ServiceBusMessage("session msg");
            sessionMessage.setSessionId(sessionId);
            sessionSender.sendMessage(sessionMessage);
        } catch (Exception e) {
            failures.add("session send: " + e);
        }

        try (ServiceBusSessionReceiverClient sessionClient = new ServiceBusClientBuilder()
                .connectionString(conn).retryOptions(retry)
                .sessionReceiver().queueName("smoke-sessions").disableAutoComplete().buildClient();
             ServiceBusReceiverClient session = sessionClient.acceptSession(sessionId)) {

            boolean received = false;
            for (ServiceBusReceivedMessage m : session.receiveMessages(1, Duration.ofSeconds(10))) {
                received = "session msg".equals(m.getBody().toString());
                session.complete(m);
            }
            if (received) {
                System.out.println("PASS session receive");
            } else {
                failures.add("session receive: no/wrong message within 10s");
            }
        } catch (Exception e) {
            failures.add("session receive: " + e);
        }

        if (!failures.isEmpty()) {
            failures.forEach(f -> System.out.println("FAIL " + f));
            System.out.println("JAVA SMOKE: FAILED");
            System.exit(1);
        }
        System.out.println("JAVA SMOKE: ALL PASS");
        System.exit(0);
    }
}
