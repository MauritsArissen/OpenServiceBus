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
    public async Task SingleClient_ThreeHundredConcurrentReceivers_AllAttachAndConnectionStaysUsable()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "crowded" });
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "probe" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var receivers = Enumerable.Range(0, 300).Select(_ => client.CreateReceiver("crowded")).ToList();

        // Attaching is lazy, so an empty receive forces every link (and its session) open;
        // with the old 255-sessions-per-connection default the attaches past 255 threw
        // ServiceBusException(QuotaExceeded) here.
        await Task.WhenAll(receivers.Select(r => r.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100))));

        // Links 301 and 302 on a separate queue prove the connection still carries traffic.
        // The probe gets its own queue because a timed-out receive leaves its credit
        // outstanding, so a message on the crowded queue could land on any of the 300 links -
        // racing them all made the assertion timing-dependent on slow CI runners.
        var sender = client.CreateSender("probe");
        await sender.SendMessageAsync(new ServiceBusMessage("fits") { MessageId = "many-1" });
        var probe = client.CreateReceiver("probe");
        var received = await probe.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull("a link opened after 300 others must still send and receive");
        received.MessageId.ShouldBe("many-1");
        await probe.CompleteMessageAsync(received);

        await Task.WhenAll(receivers.Select(r => r.DisposeAsync().AsTask()));
        await probe.DisposeAsync();
        await sender.DisposeAsync();
    }
}
