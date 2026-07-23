using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// The Azure SDK issues an AMQP drain before closing receivers and processors. The broker
/// must answer it (SourceLinkEndpoint.CompleteDrain) or every graceful close stalls for the
/// client's full 60-second timeout - which shows up in real apps as "shutdown takes a minute
/// per receiver".
/// </summary>
public class DrainTests
{
    [Fact]
    public async Task Processor_StopAndDispose_OnIdleQueue_CompletesPromptly()
    {
        // Regression: QueueReceiverSource.GetMessageAsync blocked indefinitely on the store,
        // so the endpoint never observed IsDraining and CloseAsync timed out after 60s.

        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "drain-idle" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var processor = client.CreateProcessor("drain-idle", new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = true,
            MaxConcurrentCalls = 4,
        });
        processor.ProcessMessageAsync += _ => Task.CompletedTask;
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        await processor.StartProcessingAsync();
        await Task.Delay(300); // let the receive pump go idle with outstanding credit

        // Act
        var stopwatch = Stopwatch.StartNew();
        await processor.StopProcessingAsync();
        await processor.DisposeAsync();
        stopwatch.Stop();

        // Assert - generous bound: the drain round-trip is ~200ms, the old bug was 60s.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10),
            "graceful processor close must complete the drain instead of timing out");
    }

    [Fact]
    public async Task Receiver_GracefulClose_AfterEmptyReceive_CompletesPromptly()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "drain-recv" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var receiver = client.CreateReceiver("drain-recv");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(300))).ShouldBeNull();

        // Act
        var stopwatch = Stopwatch.StartNew();
        await receiver.CloseAsync();
        stopwatch.Stop();

        // Assert
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
