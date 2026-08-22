using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

public class SessionProcessorTests
{
    [Fact]
    public async Task SessionProcessor_MaxConcurrentSessions_CapsSessionConcurrencyAndDrainsAll()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "sconc", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("sconc");
        for (var s = 0; s < 4; s++)
        {
            for (var i = 0; i < 3; i++)
            {
                await sender.SendMessageAsync(new ServiceBusMessage($"s{s}-m{i}") { SessionId = $"sess-{s}" });
            }
        }

        var activeSessions = new ConcurrentDictionary<string, byte>();
        var maxObserved = 0;
        var processed = 0;
        var perSession = new ConcurrentDictionary<string, int>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateSessionProcessor("sconc", new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = 2,
            MaxConcurrentCallsPerSession = 1,
            AutoCompleteMessages = true,
            SessionIdleTimeout = TimeSpan.FromSeconds(1),
        });
        processor.ProcessMessageAsync += async args =>
        {
            activeSessions.TryAdd(args.SessionId, 0);
            InterlockedMax(ref maxObserved, activeSessions.Count);
            await Task.Delay(100);
            activeSessions.TryRemove(args.SessionId, out _);
            perSession.AddOrUpdate(args.SessionId, 1, (_, n) => n + 1);
            if (Interlocked.Increment(ref processed) == 12) allDone.TrySetResult();
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        // Act
        await processor.StartProcessingAsync();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await processor.StopProcessingAsync();

        // Assert
        processed.ShouldBe(12);
        perSession.Keys.Count.ShouldBe(4, "every session must eventually be drained");
        perSession.Values.ShouldAllBe(n => n == 3);
        maxObserved.ShouldBeLessThanOrEqualTo(2, "no more than MaxConcurrentSessions sessions may be in flight at once");
    }

    [Fact]
    public async Task SessionProcessor_SessionLockLost_SurfacesThroughProcessErrorAsync()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "slost",
            RequiresSession = true,
            LockDuration = TimeSpan.FromSeconds(5),
        });
        await using var client = new ServiceBusClient(harness.ConnectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 1, Delay = TimeSpan.FromMilliseconds(100) },
        });
        await client.CreateSender("slost").SendMessageAsync(new ServiceBusMessage("slow work") { SessionId = "s1" });

        var reasons = new ConcurrentQueue<ServiceBusFailureReason>();
        var errored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateSessionProcessor("slost", new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = true,
            MaxConcurrentSessions = 1,
            MaxAutoLockRenewalDuration = TimeSpan.Zero,
        });
        processor.ProcessMessageAsync += async args =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
        };
        processor.ProcessErrorAsync += args =>
        {
            if (args.Exception is ServiceBusException sbEx)
            {
                reasons.Enqueue(sbEx.Reason);
                errored.TrySetResult();
            }
            return Task.CompletedTask;
        };

        // Act
        await processor.StartProcessingAsync();
        await errored.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await processor.StopProcessingAsync();

        // Assert
        reasons.ShouldContain(
            ServiceBusFailureReason.SessionLockLost,
            "a handler outrunning the session lock with renewal disabled must surface SessionLockLost");
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
