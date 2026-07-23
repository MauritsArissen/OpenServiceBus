using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Samples.FunctionsTriggerDemo.Functions;

/// <summary>
/// The DLQ is itself a triggerable sub-entity. This handler shows you the failure reason and
/// description that the broker (or the SDK's <c>DeadLetterMessageAsync</c> call) attached.
/// </summary>
public sealed class DeadLetterWatcher
{
    private readonly ILogger<DeadLetterWatcher> _logger;

    public DeadLetterWatcher(ILogger<DeadLetterWatcher> logger) => _logger = logger;

    [Function(nameof(OnDeadLetter))]
    public void OnDeadLetter(
        [ServiceBusTrigger("orders/$DeadLetterQueue", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        _logger.LogWarning(
            "[orders DLQ] id={MessageId} reason={Reason} description={Description} source={Source} body={Body}",
            message.MessageId,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            message.DeadLetterSource,
            message.Body);
    }
}
