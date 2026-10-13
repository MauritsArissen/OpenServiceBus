using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using Amqp.Types;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Amqp.WireTests;

/// <summary>
/// Send-via sender attaches (issue #58): the Java SDK's cross-entity transaction shape
/// (and the legacy track-1 send-via) attaches a sender whose TARGET is the via entity and
/// whose <c>com.microsoft:transfer-destination-address</c> link property names the real
/// destination. The broker skips the physical transfer-queue hop and routes transfers on
/// such a link straight to the destination entity.
/// </summary>
public class SendViaWireTests
{
    private static readonly Symbol TransferDestinationAddress = new("com.microsoft:transfer-destination-address");

    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    private static Attach ViaSenderAttach(string viaEntity, string destination) => new()
    {
        Target = new Target { Address = viaEntity },
        Source = new Source(),
        Properties = new Fields { [TransferDestinationAddress] = destination },
    };

    [Fact]
    public async Task Send_ViaSenderAttach_RoutesToTransferDestination()
    {
        // Arrange
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "via-a" });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "dest-b" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "via-sender", ViaSenderAttach("via-a", "dest-b"), null);

            // Act
            await sender.SendAsync(new Message("via payload") { Properties = new Properties { MessageId = "via-1" } });

            // Assert - the message lands on the destination, not on the via entity.
            (await harness.Store.CountAsync("dest-b")).ShouldBe(1L);
            (await harness.Store.CountAsync("via-a")).ShouldBe(0L);

            await sender.CloseAsync();
            await session.CloseAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task Attach_ViaEntityMissing_IsRejectedWithNotFound()
    {
        // Arrange - only the destination exists; the via entity (the attach target) does not.
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "dest-b" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Act + Assert
            var ex = await Should.ThrowAsync<AmqpException>(async () =>
            {
                var sender = new SenderLink(session, "via-sender", ViaSenderAttach("missing-via", "dest-b"), null);
                await sender.SendAsync(new Message("never"));
            });
            ex.Error.Condition.ToString().ShouldBe(ErrorCode.NotFound);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task Attach_TransferDestinationMissing_IsRejectedWithNotFound()
    {
        // Arrange - the via entity exists but the destination named by the property does not.
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "via-a" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Act + Assert
            var ex = await Should.ThrowAsync<AmqpException>(async () =>
            {
                var sender = new SenderLink(session, "via-sender", ViaSenderAttach("via-a", "missing-dest"), null);
                await sender.SendAsync(new Message("never"));
            });
            ex.Error.Condition.ToString().ShouldBe(ErrorCode.NotFound);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
