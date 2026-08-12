using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Time.Testing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Testing;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Topic-level duplicate detection through the real Azure SDK (issue #29): dedup runs at
/// the topic BEFORE fan-out, so a duplicate MessageId inside the window reaches zero
/// subscriptions, the window slides with TimeProvider, and the flag is immutable and
/// round-trips through the admin client.
/// </summary>
public class TopicDuplicateDetectionTests
{
    private static async Task<OpenServiceBusTestHost> StartWithDedupTopicAsync(
        FakeTimeProvider? clock = null, TimeSpan? window = null, params string[] subscriptions)
    {
        var host = await OpenServiceBusTestHost.StartAsync(o =>
        {
            if (clock is not null) o.TimeProvider = clock;
        });
        await host.CreateTopicAsync(new TopicDescriptor
        {
            Name = "dedup-topic",
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = window,
        });
        foreach (var sub in subscriptions)
        {
            await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "dedup-topic", Name = sub });
        }
        return host;
    }

    private static async Task<List<string>> DrainAsync(ServiceBusClient client, string topic, string sub, int max = 10)
    {
        var receiver = client.CreateReceiver(topic, sub);
        var bodies = new List<string>();
        while (bodies.Count < max)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
            if (msg is null) break;
            bodies.Add(msg.Body.ToString());
            await receiver.CompleteMessageAsync(msg);
        }
        await receiver.CloseAsync();
        return bodies;
    }

    [Fact]
    public async Task SameMessageIdTwiceWithinTheWindow_EachSubscriptionReceivesExactlyOneCopy()
    {
        await using var host = await StartWithDedupTopicAsync(subscriptions: ["a", "b"]);
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("dedup-topic");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "evt-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("retry") { MessageId = "evt-1" });

        foreach (var sub in new[] { "a", "b" })
        {
            var received = await DrainAsync(client, "dedup-topic", sub);
            received.Count.ShouldBe(1, $"subscription '{sub}' must see exactly one copy - dedup runs before fan-out");
            received[0].ShouldBe("first");
        }
    }

    [Fact]
    public async Task SameMessageIdAfterTheWindowElapses_IsDeliveredAgain()
    {
        var clock = new FakeTimeProvider();
        await using var host = await StartWithDedupTopicAsync(clock, TimeSpan.FromMinutes(1), "all");
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("dedup-topic");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "evt-2" });
        clock.Advance(TimeSpan.FromMinutes(2));
        await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "evt-2" });

        var received = await DrainAsync(client, "dedup-topic", "all");
        received.Count.ShouldBe(2, "the window elapsed, so the id is fresh again");
    }

    [Fact]
    public async Task BatchedEnvelopeWithDuplicateIds_EachInnerMessageIsCheckedIndividually()
    {
        await using var host = await StartWithDedupTopicAsync(subscriptions: ["all"]);
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("dedup-topic");
        await sender.SendMessagesAsync(new List<ServiceBusMessage>
        {
            new("one") { MessageId = "evt-3" },
            new("dup-of-one") { MessageId = "evt-3" },
            new("two") { MessageId = "evt-4" },
        });

        var received = await DrainAsync(client, "dedup-topic", "all");
        received.Count.ShouldBe(2, "the in-batch duplicate must be dropped, the distinct ids kept");
        received.ShouldContain("one");
        received.ShouldContain("two");
    }

    [Fact]
    public async Task ScheduledPublish_IsDeduplicatedAtSendTime_NotAtActivation()
    {
        var clock = new FakeTimeProvider();
        await using var host = await StartWithDedupTopicAsync(clock, TimeSpan.FromMinutes(30), "all");
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("dedup-topic");
        await sender.SendMessageAsync(new ServiceBusMessage("scheduled")
        {
            MessageId = "evt-5",
            ScheduledEnqueueTime = clock.GetUtcNow().AddMinutes(5),
        });
        // Same id sent immediately afterwards: dedup already recorded the id at SEND time
        // of the scheduled message, so this one is silently dropped - matching Azure.
        await sender.SendMessageAsync(new ServiceBusMessage("immediate") { MessageId = "evt-5" });

        (await DrainAsync(client, "dedup-topic", "all")).ShouldBeEmpty(
            "the scheduled original is not due yet and the immediate duplicate was dropped");

        clock.Advance(TimeSpan.FromMinutes(6));
        var afterActivation = await DrainAsync(client, "dedup-topic", "all");
        afterActivation.Count.ShouldBe(1);
        afterActivation[0].ShouldBe("scheduled");
    }

    [Fact]
    public async Task TopicWithoutDedup_DeliversBothCopies()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        await host.CreateTopicAsync("plain-topic");
        await host.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "plain-topic", Name = "all" });
        await using var client = new ServiceBusClient(host.ConnectionString);

        var sender = client.CreateSender("plain-topic");
        await sender.SendMessageAsync(new ServiceBusMessage("one") { MessageId = "evt-6" });
        await sender.SendMessageAsync(new ServiceBusMessage("two") { MessageId = "evt-6" });

        (await DrainAsync(client, "plain-topic", "all")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Descriptor_RoundTripsThroughTheAdminClient()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);

        await admin.CreateTopicAsync(new CreateTopicOptions("admin-dedup")
        {
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(5),
        });

        TopicProperties fetched = await admin.GetTopicAsync("admin-dedup");
        fetched.RequiresDuplicateDetection.ShouldBeTrue();
        fetched.DuplicateDetectionHistoryTimeWindow.ShouldBe(TimeSpan.FromMinutes(5));

        var broker = await host.Topics.GetTopicAsync("admin-dedup");
        broker.ShouldNotBeNull();
        broker.RequiresDuplicateDetection.ShouldBeTrue();
        broker.DuplicateDetectionHistoryTimeWindow.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Update_FlippingRequiresDuplicateDetection_IsRejectedLikeRealServiceBus()
    {
        await using var host = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(host.ConnectionString);
        await admin.CreateTopicAsync(new CreateTopicOptions("immutable-dedup") { RequiresDuplicateDetection = true });

        // The SDK's TopicProperties exposes no setter for the flag (real Azure rejects the
        // change, so the client forbids it too) - exercise the broker guard the way a raw
        // ATOM client would: an update PUT (If-Match) flipping the value.
        using var http = new HttpClient();
        const string body = """
            <entry xmlns="http://www.w3.org/2005/Atom"><content type="application/xml">
            <TopicDescription xmlns="http://schemas.microsoft.com/netservices/2010/10/servicebus/connect">
            <RequiresDuplicateDetection>false</RequiresDuplicateDetection>
            </TopicDescription></content></entry>
            """;
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"http://localhost:{host.Port}/immutable-dedup?api-version=2021-05")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/atom+xml"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("RequiresDuplicateDetection");
        (await host.Topics.GetTopicAsync("immutable-dedup"))!.RequiresDuplicateDetection.ShouldBeTrue(
            "the rejected update must not have taken effect");
    }
}
