using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Samples.FunctionsTriggerDemo.Functions;

/// <summary>
/// Plain peek-lock trigger. AutoComplete is on (host.json default), so a clean return ⇒ Complete,
/// an unhandled exception ⇒ Abandon (re-delivery). After <c>MaxDeliveryCount</c> abandons the
/// broker auto-dead-letters the message - pick that up in <see cref="DeadLetterWatcher"/>.
/// </summary>
/// <remarks>
/// Try it: send a message via the Explorer with body <c>fail-anything</c> to force a throw.
/// Three rounds later it'll show up in <c>orders/$DeadLetterQueue</c>.
/// </remarks>
public sealed class OrderProcessor
{
    private readonly ILogger<OrderProcessor> _logger;

    public OrderProcessor(ILogger<OrderProcessor> logger) => _logger = logger;

    [Function(nameof(OnOrder))]
    public async Task OnOrder(
        [ServiceBusTrigger("orders", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var body = message.Body.ToString();
        _logger.LogInformation(
            "[orders] received id={MessageId} seq={Sequence} deliveryCount={Count} body={Body}",
            message.MessageId, message.SequenceNumber, message.DeliveryCount, body);

        if (body.StartsWith("fail", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"simulated failure for message '{message.MessageId}'");
        }

        if (body.StartsWith("slow", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately exceeds lock duration if it's <2s - demonstrates lock renewal.
            _logger.LogInformation("[orders] processing slowly to exercise lock renewal…");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
