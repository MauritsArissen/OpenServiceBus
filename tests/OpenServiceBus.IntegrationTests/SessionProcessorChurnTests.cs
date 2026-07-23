using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// ServiceBusSessionProcessor recycles its slots as sessions come and go, and the SDK
/// ABORTS superseded pending accept-next-session links locally - no detach ever reaches
/// the broker. A parked accept on such a zombie link must not be able to strand a session:
/// the broker's watchdog closes any parked-accept link that never starts pumping, releasing
/// the session for a live waiter.
/// </summary>
public class SessionProcessorChurnTests
{
    [Fact]
    public async Task SessionProcessor_AfterSlotChurn_StillPicksUpAScheduledSessionPromptly()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "churn", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { Delay = TimeSpan.FromMilliseconds(100) },
        });

        var received = new ConcurrentQueue<string>();
        var processor = client.CreateSessionProcessor("churn", new ServiceBusSessionProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = true,
            MaxConcurrentSessions = 4,
            MaxConcurrentCallsPerSession = 1,
            SessionIdleTimeout = TimeSpan.FromSeconds(2),
        });
        processor.ProcessMessageAsync += args =>
        {
            received.Enqueue(args.Message.Body.ToString());
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync();
        var sender = client.CreateSender("churn");

        // Sequential session churn: every idle-close makes the SDK abort its sibling
        // pending accepts and open fresh ones, leaving zombie parked links broker-side.
        for (var i = 0; i < 3; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"warm-{i}") { SessionId = $"warm-{i}" });
            await Task.Delay(1200);
        }

        // Act: a session that only comes into existence later, via broker-side scheduling.
        var stopwatch = Stopwatch.StartNew();
        await sender.ScheduleMessageAsync(
            new ServiceBusMessage("late") { SessionId = "late-session" },
            DateTimeOffset.UtcNow.AddSeconds(2));
        while (!received.Contains("late") && stopwatch.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100);
        }

        await processor.StopProcessingAsync();
        await processor.DisposeAsync();

        // Assert: 2s schedule + pickup; the zombie watchdog bounds the worst case well
        // under this. Before the fix a zombie held the session for the full lock duration.
        received.Contains("late").ShouldBeTrue("the scheduled session must reach the processor");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(12));
    }
}
