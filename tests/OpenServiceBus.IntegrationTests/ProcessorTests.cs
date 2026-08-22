using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

public class ProcessorTests
{
    [Fact]
    public async Task Processor_MaxConcurrentCalls_CapsHandlerConcurrency()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "conc" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("conc");
        for (var i = 0; i < 8; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"m-{i}"));
        }

        var inFlight = 0;
        var maxObserved = 0;
        var processed = 0;
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateProcessor("conc", new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 2,
            AutoCompleteMessages = true,
        });
        processor.ProcessMessageAsync += async args =>
        {
            var current = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxObserved, current);
            await Task.Delay(200);
            Interlocked.Decrement(ref inFlight);
            if (Interlocked.Increment(ref processed) == 8) allDone.TrySetResult();
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        // Act
        await processor.StartProcessingAsync();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await processor.StopProcessingAsync();

        // Assert
        processed.ShouldBe(8);
        maxObserved.ShouldBe(2, "8 queued messages with 200 ms handlers must saturate both slots and never exceed them");
    }

    [Fact]
    public async Task Processor_AutoComplete_OnHandlerSuccess_SettlesTheMessage()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "autoc" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("autoc").SendMessageAsync(new ServiceBusMessage("done") { MessageId = "ac-1" });

        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateProcessor("autoc", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = true,
        });
        processor.ProcessMessageAsync += args =>
        {
            handled.TrySetResult();
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        // Act
        await processor.StartProcessingAsync();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await processor.StopProcessingAsync();

        // Assert
        var receiver = client.CreateReceiver("autoc");
        (await receiver.PeekMessageAsync(fromSequenceNumber: 1)).ShouldBeNull("auto-complete must have settled the message");
    }

    [Fact]
    public async Task Processor_HandlerThrows_MessageIsAbandonedAndRedelivered()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "throwq" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        await client.CreateSender("throwq").SendMessageAsync(new ServiceBusMessage("flaky") { MessageId = "t-1" });

        var deliveryCounts = new ConcurrentQueue<int>();
        var secondDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateProcessor("throwq", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = true,
        });
        processor.ProcessMessageAsync += args =>
        {
            deliveryCounts.Enqueue(args.Message.DeliveryCount);
            if (deliveryCounts.Count == 1) throw new InvalidOperationException("first attempt fails");
            secondDelivery.TrySetResult();
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        // Act
        await processor.StartProcessingAsync();
        await secondDelivery.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await processor.StopProcessingAsync();

        // Assert
        deliveryCounts.ToArray().ShouldBe([1, 2], "the thrown-at delivery must be abandoned and redelivered with a bumped count");
        var receiver = client.CreateReceiver("throwq");
        (await receiver.PeekMessageAsync(fromSequenceNumber: 1)).ShouldBeNull("the successful second attempt must auto-complete");
    }

    [Fact]
    public async Task Processor_EntityDeletedWhileProcessing_SurfacesThroughProcessErrorAsync()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "doomed" });
        await using var client = new ServiceBusClient(harness.ConnectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 1, Delay = TimeSpan.FromMilliseconds(100) },
        });

        var errors = new ConcurrentQueue<Exception>();
        var errored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateProcessor("doomed");
        processor.ProcessMessageAsync += _ => Task.CompletedTask;
        processor.ProcessErrorAsync += args =>
        {
            errors.Enqueue(args.Exception);
            errored.TrySetResult();
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync();

        // Act
        await harness.Queues.DeleteAsync("doomed");
        await errored.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await processor.StopProcessingAsync();

        // Assert
        errors.ShouldContain(e => e is ServiceBusException, "deleting the entity under a live processor must surface a ServiceBusException");
    }

    [Fact]
    public async Task Processor_StopProcessingAsync_StopsCleanlyAndLeavesLaterMessagesUnconsumed()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "stopq" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("stopq");
        await sender.SendMessageAsync(new ServiceBusMessage("before") { MessageId = "s-1" });

        var handled = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateProcessor("stopq", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = true,
        });
        processor.ProcessMessageAsync += args =>
        {
            Interlocked.Increment(ref handled);
            first.TrySetResult();
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Act
        await processor.StopProcessingAsync();
        await sender.SendMessageAsync(new ServiceBusMessage("after") { MessageId = "s-2" });
        await Task.Delay(1000);

        // Assert
        handled.ShouldBe(1, "a stopped processor must not consume messages sent after the stop");
        var receiver = client.CreateReceiver("stopq");
        var leftover = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        leftover.ShouldNotBeNull("the post-stop message must still be available to a fresh receiver");
        leftover.MessageId.ShouldBe("s-2");
        await receiver.CompleteMessageAsync(leftover);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref location);
        }
        while (value > current && Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
