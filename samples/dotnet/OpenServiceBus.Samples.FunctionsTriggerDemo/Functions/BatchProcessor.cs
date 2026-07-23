using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Samples.FunctionsTriggerDemo.Functions;

/// <summary>
/// With <c>IsBatched = true</c> the trigger receives an array of messages per invocation
/// (up to host.json <c>maxMessageBatchSize</c> messages). Each one is independently settled by
/// auto-complete on successful return.
/// </summary>
public sealed class BatchProcessor
{
    private readonly ILogger<BatchProcessor> _logger;

    public BatchProcessor(ILogger<BatchProcessor> logger) => _logger = logger;

    [Function(nameof(OnBatch))]
    public void OnBatch(
        [ServiceBusTrigger("batch-queue", Connection = "ServiceBusConnection", IsBatched = true)]
        ServiceBusReceivedMessage[] batch)
    {
        _logger.LogInformation("[batch-queue] received batch of {Count} message(s)", batch.Length);
        foreach (var message in batch)
        {
            _logger.LogInformation(
                "[batch-queue]   - id={MessageId} seq={Sequence} body={Body}",
                message.MessageId, message.SequenceNumber, message.Body);
        }
    }
}
