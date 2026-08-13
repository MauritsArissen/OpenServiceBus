using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using Amqp.Types;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Amqp.WireTests;

/// <summary>
/// Raw-AMQP coverage for PropertiesToModify (issue #30): a Modified disposition whose
/// message-annotations field carries the map merges it into the redelivered message.
/// </summary>
public class PropertiesToModifyWireTests
{
    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    [Fact]
    public async Task Modify_WithMessageAnnotations_RedeliversWithMergedApplicationProperties()
    {
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "ptm-wire" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "s", "ptm-wire");
            var original = new Message("wire-body") { Properties = new Properties { MessageId = "w-1" } };
            original.ApplicationProperties = new ApplicationProperties();
            original.ApplicationProperties["existing"] = "old";
            await sender.SendAsync(original);

            var receiver = new ReceiverLink(session, "r", "ptm-wire");
            var first = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
            first.ShouldNotBeNull();

            var annotations = new Fields();
            annotations.Add(new Symbol("retry-reason"), "wire-timeout");
            annotations.Add(new Symbol("existing"), "new");
            receiver.Modify(first, deliveryFailed: true, undeliverableHere: false, messageAnnotations: annotations);

            var redelivered = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
            redelivered.ShouldNotBeNull();
            redelivered.Properties?.MessageId.ShouldBe("w-1");
            (redelivered.ApplicationProperties["retry-reason"] as string).ShouldBe("wire-timeout");
            (redelivered.ApplicationProperties["existing"] as string).ShouldBe("new");
            redelivered.Header?.DeliveryCount.ShouldBe(1u);
            receiver.Accept(redelivered);

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
