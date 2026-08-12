using Amqp;
using Amqp.Sasl;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Amqp.WireTests;

/// <summary>
/// A receiver closed with credit still outstanding (pyamqp closes without draining on a
/// receive timeout) must not leave a zombie dequeue pump behind: messages sent AFTER the
/// close belong to the next receiver, not to the dead link's in-flight poll.
/// </summary>
public class ReceiverDetachTests
{
    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    [Fact]
    public async Task CloseWithOutstandingCredit_ThenSend_TheNextReceiverGetsTheMessage()
    {
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "no-zombie" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);

            // Open a receiver on the empty queue, hand the broker credit, get nothing,
            // then close WITHOUT draining - exactly what pyamqp does on a receive timeout.
            var doomed = new ReceiverLink(session, "r-doomed", "no-zombie");
            doomed.SetCredit(5, autoRestore: false);
            (await doomed.ReceiveAsync(TimeSpan.FromMilliseconds(500))).ShouldBeNull();
            await doomed.CloseAsync();

            var sender = new SenderLink(session, "s", "no-zombie");
            await sender.SendAsync(new Message("late") { Properties = new global::Amqp.Framing.Properties { MessageId = "late-1" } });

            // Give a would-be zombie pump time to steal the message before we attach.
            await Task.Delay(600);

            var receiver = new ReceiverLink(session, "r-live", "no-zombie");
            var msg = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));
            msg.ShouldNotBeNull("the message sent after the old receiver closed belongs to the new receiver");
            msg.Properties?.MessageId.ShouldBe("late-1");
            receiver.Accept(msg);

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
