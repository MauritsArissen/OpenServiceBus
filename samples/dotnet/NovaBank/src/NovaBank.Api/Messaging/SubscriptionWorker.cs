using Azure.Messaging.ServiceBus;

namespace NovaBank.Api.Messaging;

/// <summary>
/// Base class for the three topic-subscription consumers (audit, fraud, notifications).
/// Single-threaded per subscription so projections observe events in delivery order.
/// </summary>
public abstract class SubscriptionWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly string _topic;
    private readonly string _subscription;
    private readonly ILogger _logger;

    protected SubscriptionWorker(ServiceBusClient client, string topic, string subscription, ILogger logger)
    {
        _client = client;
        _topic = topic;
        _subscription = subscription;
        _logger = logger;
    }

    protected abstract Task HandleAsync(IntegrationEvent evt, ServiceBusReceivedMessage message, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = _client.CreateProcessor(_topic, _subscription, new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = true,
            MaxConcurrentCalls = 1,
        });

        processor.ProcessMessageAsync += async args =>
        {
            var evt = args.Message.Body.ToObjectFromJson<IntegrationEvent>(BusJson.Options);
            if (evt is null)
            {
                _logger.LogWarning("Discarding unreadable event {MessageId} on {Topic}/{Subscription}.",
                    args.Message.MessageId, _topic, _subscription);
                return;
            }
            await HandleAsync(evt, args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogWarning(args.Exception, "{Topic}/{Subscription} processor error (source={Source}).",
                _topic, _subscription, args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Listening on {Topic}/{Subscription}.", _topic, _subscription);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutdown */ }

        await processor.StopProcessingAsync(CancellationToken.None);
        await processor.DisposeAsync();
    }
}
