using Amqp;
using Amqp.Framing;
using Amqp.Sasl;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Amqp.WireTests;

/// <summary>
/// Deleting an entity must proactively detach the links attached to it with
/// <c>amqp:not-found</c> (issue #36): a receiver pump that keeps waiting on a deleted
/// queue leaves AMQPNetLite's SourceLinkEndpoint stuck in "receiving", so the drain the
/// Azure SDK issues on every graceful close is never answered and CloseAsync stalls for
/// the client's full 60-second timeout.
/// </summary>
public class EntityDeletionWireTests
{
    private static ConnectionFactory CreateClientFactory()
    {
        var factory = new ConnectionFactory();
        factory.SASL.Profile = SaslProfile.Anonymous;
        return factory;
    }

    [Fact]
    public async Task DeletingQueue_DetachesLiveReceiverLink_WithNotFound()
    {
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "vanish" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var receiver = new ReceiverLink(session, "receiver", "vanish");
            var closed = new TaskCompletionSource<Error?>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.Closed += (_, error) => closed.TrySetResult(error);
            var pending = receiver.ReceiveAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(300);

            await harness.Queues.DeleteAsync("vanish");

            var winner = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            winner.ShouldBe(closed.Task, "the broker should detach the receiver link once the queue is deleted");
            var error = await closed.Task;
            error.ShouldNotBeNull();
            error.Condition.ToString().ShouldBe("amqp:not-found");

            try
            {
                (await pending).ShouldBeNull();
            }
            catch (AmqpException ex)
            {
                ex.Error?.Condition.ToString().ShouldBe("amqp:not-found");
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task DeletingQueue_RejectsInFlightSends_WithNotFound()
    {
        await using var harness = await TestListenerHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "gone" });
        var factory = CreateClientFactory();
        var conn = await factory.CreateAsync(new Address(harness.AmqpUri));
        try
        {
            var session = new Session(conn);
            var sender = new SenderLink(session, "sender", "gone");
            await sender.SendAsync(new Message("before-delete"));

            await harness.Queues.DeleteAsync("gone");

            var ex = await Should.ThrowAsync<AmqpException>(
                () => sender.SendAsync(new Message("after-delete")));
            ex.Error?.Condition.ToString().ShouldBe("amqp:not-found");
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
