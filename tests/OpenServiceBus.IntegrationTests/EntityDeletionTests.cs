using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Deleting entities that still have live SDK clients attached (issue #36). The broker
/// detaches those links with <c>amqp:not-found</c> so receivers close promptly instead of
/// stalling on the SDK's 60-second "object drain" timeout, sends surface
/// <see cref="ServiceBusFailureReason.MessagingEntityNotFound"/>, and processors reconnect
/// on their own once the entity is recreated.
/// </summary>
public class EntityDeletionTests
{
    [Fact]
    public async Task Receiver_WithPendingReceive_ClosesPromptly_AfterQueueIsDeleted()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("doomed");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var receiver = client.CreateReceiver("doomed");
        var pending = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(500);

        await admin.DeleteQueueAsync("doomed");
        await Task.Delay(500);

        var stopwatch = Stopwatch.StartNew();
        await receiver.CloseAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10),
            "closing a receiver whose queue was deleted must not hit the 60s drain timeout");
        (await pending).ShouldBeNull();
    }

    [Fact]
    public async Task SessionReceiver_WithPendingReceive_ClosesPromptly_AfterQueueIsDeleted()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "doomed-sessions", RequiresSession = true });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var sender = client.CreateSender("doomed-sessions");
        await sender.SendMessageAsync(new ServiceBusMessage("hi") { SessionId = "s-1" });
        var receiver = await client.AcceptSessionAsync("doomed-sessions", "s-1");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10))).ShouldNotBeNull();
        var pending = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(500);

        await harness.Queues.DeleteAsync("doomed-sessions");
        await Task.Delay(500);

        var stopwatch = Stopwatch.StartNew();
        await receiver.CloseAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        (await pending).ShouldBeNull();
    }

    [Fact]
    public async Task Send_AfterQueueIsDeleted_ThrowsMessagingEntityNotFound()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("erased");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var sender = client.CreateSender("erased");
        await sender.SendMessageAsync(new ServiceBusMessage("before"));

        await admin.DeleteQueueAsync("erased");

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => sender.SendMessageAsync(new ServiceBusMessage("after")));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityNotFound);
    }

    [Fact]
    public async Task Send_AfterTopicIsDeleted_ThrowsMessagingEntityNotFound()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("erased-topic");
        await admin.CreateSubscriptionAsync("erased-topic", "sub");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var sender = client.CreateSender("erased-topic");
        await sender.SendMessageAsync(new ServiceBusMessage("before"));

        await admin.DeleteTopicAsync("erased-topic");

        var ex = await Should.ThrowAsync<ServiceBusException>(
            () => sender.SendMessageAsync(new ServiceBusMessage("after")));
        ex.Reason.ShouldBe(ServiceBusFailureReason.MessagingEntityNotFound);
    }

    [Fact]
    public async Task Complete_AfterQueueIsDeleted_DoesNotFaultTheConnection()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateQueueAsync("settle-gone");
        await admin.CreateQueueAsync("still-here");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        await client.CreateSender("settle-gone").SendMessageAsync(new ServiceBusMessage("hi"));
        var receiver = client.CreateReceiver("settle-gone");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        message.ShouldNotBeNull();

        await admin.DeleteQueueAsync("settle-gone");
        await Task.Delay(500);

        await receiver.CompleteMessageAsync(message);

        var probeSender = client.CreateSender("still-here");
        await probeSender.SendMessageAsync(new ServiceBusMessage("connection still works"));
        var probe = await client.CreateReceiver("still-here").ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        probe.ShouldNotBeNull();
    }

    [Fact]
    public async Task Processor_SurvivesDeleteAndRecreate_AndProcessesNewMessages()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("sbt-cycle");
        await admin.CreateSubscriptionAsync("sbt-cycle", "sbs-worker");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var processed = new List<string>();
        var processor = client.CreateProcessor("sbt-cycle", "sbs-worker");
        processor.ProcessMessageAsync += args =>
        {
            lock (processed) processed.Add(args.Message.MessageId);
            return Task.CompletedTask;
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync();

        var sender = client.CreateSender("sbt-cycle");
        await SendUntilAcceptedAsync(sender, "round-1");
        await WaitForProcessedAsync(processed, "round-1");

        await admin.DeleteSubscriptionAsync("sbt-cycle", "sbs-worker");
        await admin.DeleteTopicAsync("sbt-cycle");
        await admin.CreateTopicAsync("sbt-cycle");
        await admin.CreateSubscriptionAsync("sbt-cycle", "sbs-worker");

        await SendUntilAcceptedAsync(sender, "round-2");
        await WaitForProcessedAsync(processed, "round-2");

        var stopwatch = Stopwatch.StartNew();
        await processor.StopProcessingAsync();
        await processor.DisposeAsync();
        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10),
            "processor shutdown after a delete/recreate cycle must not hit the drain timeout");
    }

    private static async Task SendUntilAcceptedAsync(ServiceBusSender sender, string messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await sender.SendMessageAsync(new ServiceBusMessage("hi") { MessageId = messageId });
                return;
            }
            catch (ServiceBusException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static async Task WaitForProcessedAsync(List<string> processed, string messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            lock (processed)
            {
                if (processed.Contains(messageId)) return;
            }
            await Task.Delay(100);
        }
        lock (processed)
        {
            processed.ShouldContain(messageId,
                $"the processor should have reconnected and processed '{messageId}'");
        }
    }
}
