using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Samples.Functions;

/// <summary>
/// Service Bus trigger function. On each message, appends the message id to the file at
/// <c>OSB_FUNCTIONS_SENTINEL_FILE</c>; the integration test polls that file to assert delivery.
/// </summary>
public sealed class OrderProcessor
{
    private static readonly string? SentinelPath =
        Environment.GetEnvironmentVariable("OSB_FUNCTIONS_SENTINEL_FILE");

    private static readonly object Lock = new();

    private readonly ILogger<OrderProcessor> _logger;

    public OrderProcessor(ILogger<OrderProcessor> logger) => _logger = logger;

    [Function(nameof(ProcessOrder))]
    public void ProcessOrder(
        [ServiceBusTrigger("integration-queue", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        _logger.LogInformation("Processed {MessageId}", message.MessageId);

        if (!string.IsNullOrEmpty(SentinelPath))
        {
            lock (Lock)
            {
                File.AppendAllText(SentinelPath, message.MessageId + Environment.NewLine);
            }
        }
    }
}
