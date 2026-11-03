using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// One ServiceBusClient must support far more than 256 concurrent links (issue #93): the
/// Azure SDK opens a session per sender/receiver, and AMQPNetLite's default channel-max of
/// 255 made attach number 256 fail with QuotaExceeded - a ceiling real Service Bus does not
/// have. The listener now advertises and enforces the raised limits from AmqpListenerOptions.
/// </summary>
public class ManyLinksPerConnectionTests
{
    [Fact]
    public async Task SingleClient_ThreeHundredConcurrentReceivers_AllAttachAndReceive()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "busy" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var sender = client.CreateSender("busy");
        var receivers = Enumerable.Range(0, 300).Select(_ => client.CreateReceiver("busy")).ToList();

        // Attaching is lazy, so an empty receive forces every link (and its session) open;
        // with the old 255-sessions-per-connection default the attaches past 255 threw
        // ServiceBusException(QuotaExceeded) here. Opening all 300 without throwing is the
        // core of the regression check.
        await Task.WhenAll(receivers.Select(r => r.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100))));

        // One message, 300 competing receivers - any one may win it, so race them all and
        // assert that it lands on some link past the old 255 ceiling rather than on a
        // specific index.
        await sender.SendMessageAsync(new ServiceBusMessage("fits") { MessageId = "many-1" });
        var drains = receivers.Select(async r =>
        {
            var m = await r.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            if (m is not null) await r.CompleteMessageAsync(m);
            return m;
        });
        var received = (await Task.WhenAll(drains)).SingleOrDefault(m => m is not null);
        received.ShouldNotBeNull("the message must be delivered to one of the 300 live links");
        received.MessageId.ShouldBe("many-1");

        await Task.WhenAll(receivers.Select(r => r.DisposeAsync().AsTask()));
        await sender.DisposeAsync();
    }
}
