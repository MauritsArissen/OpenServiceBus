using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// A dup-detection-enabled queue silently drops repeat sends with the same
/// <c>MessageId</c>. The Azure SDK sees a normal "accepted" disposition each time, but
/// only the first message is stored and delivered.
/// </summary>
public class DuplicateDetectionTests
{
    [Fact]
    public async Task SendMessageAsync_SameMessageIdTwice_OnlyTheFirstSurvives()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "deduped",
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5),
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("deduped");

        // Act - three sends, two of which share MessageId "dup".
        await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "dup" });
        await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "dup" });
        await sender.SendMessageAsync(new ServiceBusMessage("unique") { MessageId = "other" });

        // Assert
        (await harness.Store.CountAsync("deduped")).ShouldBe(2L,
            "the duplicate 'dup' must be silently dropped; 'first' and 'unique' remain");

        var receiver = client.CreateReceiver("deduped");
        var seen = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            msg.ShouldNotBeNull();
            seen.Add(msg.Body.ToString());
            await receiver.CompleteMessageAsync(msg);
        }
        seen.ToArray().ShouldBe(new[] { "first", "unique" }, "the second 'dup' send was never enqueued");
    }

    [Fact]
    public async Task SendMessageAsync_DupOnQueueWithoutDetection_BothSurvive()
    {
        // Arrange - same scenario, but the queue is not dup-detect-enabled.
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor { Name = "no-dedup" });
        await using var client = new ServiceBusClient(harness.ConnectionString);
        var sender = client.CreateSender("no-dedup");

        // Act
        await sender.SendMessageAsync(new ServiceBusMessage("a") { MessageId = "same" });
        await sender.SendMessageAsync(new ServiceBusMessage("b") { MessageId = "same" });

        // Assert
        (await harness.Store.CountAsync("no-dedup")).ShouldBe(2L,
            "without RequiresDuplicateDetection the same MessageId is allowed twice");
    }

    [Fact]
    public async Task SendMessagesAsync_BatchContainingDuplicateIds_EachInnerMessageIsCheckedIndividually()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Queues.CreateAsync(new QueueDescriptor
        {
            Name = "dedup-batch",
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5),
        });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        // One batched AMQP envelope carrying an in-batch duplicate.
        await client.CreateSender("dedup-batch").SendMessagesAsync(new List<ServiceBusMessage>
        {
            new("one") { MessageId = "b-1" },
            new("dup-of-one") { MessageId = "b-1" },
            new("two") { MessageId = "b-2" },
        });

        (await harness.Store.CountAsync("dedup-batch")).ShouldBe(2L,
            "the duplicate inside the batch must be dropped, the distinct ids kept");
    }

    [Fact]
    public async Task ScheduleMessageAsync_ThenImmediateSendWithSameId_TheDuplicateIsDroppedAtSendTime()
    {
        var clock = new FakeTimeProvider();
        await using var host = await OpenServiceBusTestHost.StartAsync(o => o.TimeProvider = clock);
        await host.CreateQueueAsync(new QueueDescriptor
        {
            Name = "dedup-scheduled",
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(30),
        });
        await using var client = new ServiceBusClient(host.ConnectionString);
        var sender = client.CreateSender("dedup-scheduled");

        // Dedup is evaluated at send time, not at activation - so the scheduled original
        // reserves the id immediately and the plain send that follows is dropped.
        await sender.ScheduleMessageAsync(
            new ServiceBusMessage("scheduled") { MessageId = "s-1" }, clock.GetUtcNow().AddMinutes(5));
        await sender.SendMessageAsync(new ServiceBusMessage("immediate") { MessageId = "s-1" });

        var receiver = client.CreateReceiver("dedup-scheduled");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "the scheduled original is not due yet and the immediate duplicate was dropped");

        clock.Advance(TimeSpan.FromMinutes(6));
        var activated = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        activated.ShouldNotBeNull();
        activated.Body.ToString().ShouldBe("scheduled");
        await receiver.CompleteMessageAsync(activated);
    }

    [Fact]
    public async Task Update_FlippingRequiresDuplicateDetectionOnAQueue_IsRejectedLikeRealServiceBus()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.CreateQueueAsync(new QueueDescriptor { Name = "immutable-q", RequiresDuplicateDetection = true });

        // The SDK's QueueProperties exposes no setter for the flag; exercise the ATOM
        // guard the way a raw management client would.
        using var http = new HttpClient();
        const string body = """
            <entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">
            <QueueDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">
            <RequiresDuplicateDetection>false</RequiresDuplicateDetection>
            </QueueDescription></content></entry>
            """;
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"http://localhost:{host.Port}/immutable-q?api-version=2021-05")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/atom+xml"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await host.Queues.GetAsync("immutable-q"))!.RequiresDuplicateDetection.ShouldBeTrue(
            "the rejected update must not have taken effect");
    }
}
