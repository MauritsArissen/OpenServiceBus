using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

public class BatchReceiveTests
{
    [Fact]
    public async Task ReceiveMessagesAsync_EnoughAvailable_ReturnsTheFullBatchInOneCall()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "batch" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("batch");
        for (var i = 0; i < 10; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"m-{i}") { MessageId = $"b-{i}" });
        }

        // Act
        var receiver = client.CreateReceiver("batch");
        var batch = await receiver.ReceiveMessagesAsync(maxMessages: 10, maxWaitTime: TimeSpan.FromSeconds(10));

        // Assert
        batch.Count.ShouldBe(10);
        batch.Select(m => m.MessageId).ShouldBe(Enumerable.Range(0, 10).Select(i => $"b-{i}"));
        foreach (var msg in batch)
        {
            await receiver.CompleteMessageAsync(msg);
        }
        (await receiver.PeekMessageAsync(fromSequenceNumber: 1)).ShouldBeNull();
    }

    [Fact]
    public async Task ReceiveMessagesAsync_FewerAvailableThanRequested_ReturnsThePartialBatch()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "partial" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("partial");
        for (var i = 0; i < 3; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"m-{i}") { MessageId = $"p-{i}" });
        }

        // Act
        var receiver = client.CreateReceiver("partial");
        var batch = await receiver.ReceiveMessagesAsync(maxMessages: 10, maxWaitTime: TimeSpan.FromSeconds(5));

        // Assert
        batch.Count.ShouldBe(3);
        foreach (var msg in batch)
        {
            await receiver.CompleteMessageAsync(msg);
        }
    }

    [Fact]
    public async Task ReceiveMessagesAsync_EmptyQueue_HonorsMaxWaitTimeAndReturnsEmpty()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "empty" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var receiver = client.CreateReceiver("empty");

        // Act
        var stopwatch = Stopwatch.StartNew();
        var batch = await receiver.ReceiveMessagesAsync(maxMessages: 5, maxWaitTime: TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        // Assert
        batch.ShouldBeEmpty();
        stopwatch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(1.5));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ReceiveMessagesAsync_WithPrefetch_DeliversEveryMessageExactlyOnceAcrossCalls()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "prefetch" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("prefetch");
        for (var i = 0; i < 10; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"m-{i}") { MessageId = $"pf-{i}" });
        }

        // Act
        var receiver = client.CreateReceiver("prefetch", new ServiceBusReceiverOptions { PrefetchCount = 5 });
        var seen = new List<string>();
        while (seen.Count < 10)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 4, maxWaitTime: TimeSpan.FromSeconds(10));
            batch.ShouldNotBeEmpty("delivery stalled before every prefetched message arrived");
            foreach (var msg in batch)
            {
                seen.Add(msg.MessageId);
                await receiver.CompleteMessageAsync(msg);
            }
        }

        // Assert
        seen.Count.ShouldBe(10);
        seen.ShouldBeUnique();
        (await receiver.PeekMessageAsync(fromSequenceNumber: 1)).ShouldBeNull();
    }

    [Fact]
    public async Task ReceiveMessagesAsync_OnASessionReceiver_ReturnsOnlyThatSessionsMessages()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "sbatch", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("sbatch");
        for (var i = 0; i < 3; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"a-{i}") { SessionId = "sa", MessageId = $"sa-{i}" });
            await sender.SendMessageAsync(new ServiceBusMessage($"b-{i}") { SessionId = "sb", MessageId = $"sb-{i}" });
        }

        // Act
        var session = await client.AcceptSessionAsync("sbatch", "sa");
        var batch = await session.ReceiveMessagesAsync(maxMessages: 10, maxWaitTime: TimeSpan.FromSeconds(5));

        // Assert
        batch.Count.ShouldBe(3);
        batch.ShouldAllBe(m => m.SessionId == "sa");
        foreach (var msg in batch)
        {
            await session.CompleteMessageAsync(msg);
        }
    }
}
