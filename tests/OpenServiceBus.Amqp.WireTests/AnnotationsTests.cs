using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using Amqp.Types;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Amqp.WireTests;

public class AnnotationsTests
{
    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    [Fact]
    public async Task Receive_NewlyEnqueuedMessage_CarriesSequenceNumberEnqueuedTimeAndLockedUntilAnnotations()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "annotated", LockDuration = TimeSpan.FromMinutes(2) });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "s", "annotated");
            var beforeSend = DateTimeOffset.UtcNow;
            await sender.SendAsync(new Message("payload") { Properties = new Properties { MessageId = "m-1" } });
            var receiver = new ReceiverLink(session, "r", "annotated");

            // Act
            var received = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

            // Assert
            received.ShouldNotBeNull();
            received.MessageAnnotations.ShouldNotBeNull();

            var seqKey = new Symbol("x-opt-sequence-number");
            var enqueuedKey = new Symbol("x-opt-enqueued-time");
            var lockedUntilKey = new Symbol("x-opt-locked-until");

            received.MessageAnnotations.Map.ContainsKey(seqKey).ShouldBeTrue("x-opt-sequence-number missing");
            received.MessageAnnotations.Map.ContainsKey(enqueuedKey).ShouldBeTrue("x-opt-enqueued-time missing");
            received.MessageAnnotations.Map.ContainsKey(lockedUntilKey).ShouldBeTrue("x-opt-locked-until missing");

            Convert.ToInt64(received.MessageAnnotations.Map[seqKey]).ShouldBe(1L, "first message gets sequence #1");

            var enqueued = (DateTime)received.MessageAnnotations.Map[enqueuedKey];
            enqueued.Kind.ShouldBe(DateTimeKind.Utc);
            enqueued.ShouldBeGreaterThanOrEqualTo(beforeSend.UtcDateTime.AddSeconds(-1));
            enqueued.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddSeconds(1));

            var lockedUntil = (DateTime)received.MessageAnnotations.Map[lockedUntilKey];
            lockedUntil.ShouldBeGreaterThan(DateTime.UtcNow.AddSeconds(60), "lock-duration is 2min so locked-until must be ~120s out");

            receiver.Accept(received);
            await receiver.CloseAsync();
            await sender.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task Receive_Delivery_CarriesEnqueuedSequenceNumberMessageStateAndPartitionKeyAnnotations()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "stamped" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "s", "stamped");
            var message = new Message("payload")
            {
                Properties = new Properties { MessageId = "m-1" },
                MessageAnnotations = new MessageAnnotations(),
            };
            message.MessageAnnotations.Map[new Symbol("x-opt-partition-key")] = "pk-1";
            message.MessageAnnotations.Map[new Symbol("x-opt-via-partition-key")] = "via-pk-1";
            await sender.SendAsync(message);
            var receiver = new ReceiverLink(session, "r", "stamped");

            // Act
            var received = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

            // Assert
            received.ShouldNotBeNull();
            received.MessageAnnotations.ShouldNotBeNull();
            var map = received.MessageAnnotations.Map;

            map.ContainsKey(new Symbol("x-opt-enqueue-sequence-number")).ShouldBeTrue("x-opt-enqueue-sequence-number missing");
            Convert.ToInt64(map[new Symbol("x-opt-enqueue-sequence-number")]).ShouldBe(1L,
                "a direct send gets the enqueued sequence number of its own entity");
            Convert.ToInt64(map[new Symbol("x-opt-sequence-number")]).ShouldBe(1L);

            map.ContainsKey(new Symbol("x-opt-message-state")).ShouldBeTrue("x-opt-message-state missing");
            Convert.ToInt32(map[new Symbol("x-opt-message-state")]).ShouldBe(0, "a normal delivery is Active (0)");

            map[new Symbol("x-opt-partition-key")].ShouldBe("pk-1", "the sender's partition key must round-trip");
            map[new Symbol("x-opt-via-partition-key")].ShouldBe("via-pk-1", "the sender's via-partition-key must round-trip");

            receiver.Accept(received);
            await receiver.CloseAsync();
            await sender.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task Receive_RepeatedReleasesOnSameMessage_IncrementsHeaderDeliveryCountEachRedelivery()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "retries" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "s", "retries");
            await sender.SendAsync(new Message("body") { Properties = new Properties { MessageId = "m-1" } });
            var receiver = new ReceiverLink(session, "r", "retries");

            // Act
            var first = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
            first.ShouldNotBeNull();
            receiver.Release(first);
            var second = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
            second.ShouldNotBeNull();
            receiver.Release(second);
            var third = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
            third.ShouldNotBeNull();
            receiver.Accept(third);

            // Assert
            first.Header.ShouldNotBeNull();
            first.Header.DeliveryCount.ShouldBe(0u, "initial delivery must be count 0");
            second.Header.ShouldNotBeNull();
            second.Header.DeliveryCount.ShouldBe(1u, "redelivery after release must be count 1");
            third.Header.DeliveryCount.ShouldBe(2u);

            await receiver.CloseAsync();
            await sender.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
