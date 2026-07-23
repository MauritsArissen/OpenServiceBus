using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Samples.FunctionsTriggerDemo.Functions;

/// <summary>
/// With <see cref="ServiceBusTriggerAttribute.AutoCompleteMessages"/> set to false, the handler
/// is responsible for settling each message. The body of the message decides the verdict:
///   • <c>complete</c>   → CompleteMessageAsync (message removed)
///   • <c>abandon</c>    → AbandonMessageAsync (delivery-count bumps, message re-queued)
///   • <c>deadletter</c> → DeadLetterMessageAsync (to manual-queue/$DeadLetterQueue)
///   • <c>defer</c>      → DeferMessageAsync (parked; only retrievable by sequence number)
/// </summary>
public sealed class ManualDisposition
{
    private readonly ILogger<ManualDisposition> _logger;

    public ManualDisposition(ILogger<ManualDisposition> logger) => _logger = logger;

    [Function(nameof(OnManualMessage))]
    public async Task OnManualMessage(
        [ServiceBusTrigger("manual-queue", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions)
    {
        var verdict = message.Body.ToString().Trim().ToLowerInvariant();
        _logger.LogInformation(
            "[manual-queue] id={MessageId} deliveryCount={Count} verdict={Verdict}",
            message.MessageId, message.DeliveryCount, verdict);

        switch (verdict)
        {
            case "abandon":
                await actions.AbandonMessageAsync(message);
                break;

            case "deadletter":
                await actions.DeadLetterMessageAsync(
                    message,
                    deadLetterReason: "ManualReject",
                    deadLetterErrorDescription: "rejected by handler based on body");
                break;

            case "defer":
                await actions.DeferMessageAsync(message);
                _logger.LogInformation("[manual-queue] message deferred - retrieve via ReceiveDeferredMessagesAsync(seq={Seq})", message.SequenceNumber);
                break;

            default:
                await actions.CompleteMessageAsync(message);
                break;
        }
    }
}
