using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenServiceBus.Samples.WorkerService;

var host = Host.CreateApplicationBuilder(args);

host.Services.AddSingleton(_ =>
{
    var conn = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION")
        ?? "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
    return new ServiceBusClient(conn);
});

host.Services.AddHostedService<OrderProcessor>();

await host.Build().RunAsync();

namespace OpenServiceBus.Samples.WorkerService
{
    /// <summary>
    /// Idiomatic .NET background-worker pattern: a <see cref="BackgroundService"/> that owns
    /// a <see cref="ServiceBusProcessor"/>. The processor handles peek-lock, lock renewal,
    /// and concurrency; we just wire the message handler.
    /// </summary>
    public sealed class OrderProcessor : BackgroundService
    {
        private readonly ServiceBusClient _client;
        private readonly ILogger<OrderProcessor> _logger;

        public OrderProcessor(ServiceBusClient client, ILogger<OrderProcessor> logger)
        {
            _client = client;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ServiceBusProcessor manages concurrency + auto-complete + lock renewal under the hood.
            var processor = _client.CreateProcessor("work", new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = true,
                MaxConcurrentCalls = 4,
                PrefetchCount = 16,
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
            });

            processor.ProcessMessageAsync += OnMessage;
            processor.ProcessErrorAsync += OnError;

            await processor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation("Worker started; processing messages from 'work'. Press Ctrl+C to stop.");

            // Wait until the host signals shutdown.
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { /* normal */ }

            await processor.StopProcessingAsync(CancellationToken.None);
            await processor.DisposeAsync();
        }

        private async Task OnMessage(ProcessMessageEventArgs args)
        {
            var body = args.Message.Body.ToString();
            _logger.LogInformation(
                "[work] id={MessageId} delivery={DeliveryCount} body=\"{Body}\"",
                args.Message.MessageId, args.Message.DeliveryCount, body);

            if (body.StartsWith("fail", StringComparison.OrdinalIgnoreCase))
            {
                // Throwing here makes the processor abandon - the message redelivers until
                // MaxDeliveryCount, at which point the broker auto-DLQs it.
                throw new InvalidOperationException($"simulated failure for '{args.Message.MessageId}'");
            }

            // AutoComplete handles the disposition on a clean return.
            await Task.CompletedTask;
        }

        private Task OnError(ProcessErrorEventArgs args)
        {
            _logger.LogWarning(args.Exception, "[work] processor error: {Source} entity={Entity}",
                args.ErrorSource, args.EntityPath);
            return Task.CompletedTask;
        }
    }
}
