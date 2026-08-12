using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenServiceBus.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// POST /api/resend against a real broker (issue #28): a dead-lettered message is duplicated
/// back onto its source entity as a brand-new send while the DLQ original stays untouched.
/// </summary>
public class ResendEndpointTests
{
    private static async Task<long> DeadLetterAndGetSequenceAsync(
        ServiceBusClient client, string queue, string messageId, string reason = "boom")
    {
        var receiver = client.CreateReceiver(queue);
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await receiver.DeadLetterMessageAsync(msg, reason);
        await receiver.CloseAsync();

        var dlq = client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var dead = (await dlq.PeekMessagesAsync(50)).Single(m => m.MessageId == messageId);
        await dlq.CloseAsync();
        return dead.SequenceNumber;
    }

    private static async Task<JsonNode> ResendAsync(
        HttpClient http, string conn, string queue, long[] sequenceNumbers,
        string? destination = null, bool keepMessageId = false)
    {
        var resp = await http.PostAsJsonAsync("/api/resend", new
        {
            connectionString = conn,
            queue,
            sequenceNumbers,
            destination,
            keepMessageId,
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task Resend_FromQueueDlq_DeliversACleanCopyAndLeavesTheOriginal()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("resend-q");
        await using var client = new ServiceBusClient(broker.ConnectionString);
        var sender = client.CreateSender("resend-q");
        var original = new ServiceBusMessage("payload") { MessageId = "orig-1", Subject = "subj", CorrelationId = "corr" };
        original.ApplicationProperties["k"] = "v";
        await sender.SendMessageAsync(original);
        var seq = await DeadLetterAndGetSequenceAsync(client, "resend-q", "orig-1");

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        var result = await ResendAsync(http, broker.ConnectionString, "resend-q/$DeadLetterQueue", [seq]);
        result["succeeded"]!.GetValue<int>().ShouldBe(1);
        result["destination"]!.GetValue<string>().ShouldBe("resend-q");

        var receiver = client.CreateReceiver("resend-q");
        var copy = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        copy.ShouldNotBeNull();
        copy.Body.ToString().ShouldBe("payload");
        copy.Subject.ShouldBe("subj");
        copy.CorrelationId.ShouldBe("corr");
        copy.ApplicationProperties["k"].ShouldBe("v");
        copy.ApplicationProperties.ContainsKey("DeadLetterReason").ShouldBeFalse();
        copy.DeliveryCount.ShouldBe(1);
        copy.DeadLetterReason.ShouldBeNull();
        copy.MessageId.ShouldNotBe("orig-1");
        await receiver.CompleteMessageAsync(copy);

        var dlq = client.CreateReceiver("resend-q", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        (await dlq.PeekMessagesAsync(10)).ShouldContain(m => m.MessageId == "orig-1",
            "resend must never touch the DLQ original");
    }

    [Fact]
    public async Task Resend_KeepMessageId_PreservesTheOriginalId()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("resend-keep");
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await client.CreateSender("resend-keep").SendMessageAsync(new ServiceBusMessage("x") { MessageId = "orig-2" });
        var seq = await DeadLetterAndGetSequenceAsync(client, "resend-keep", "orig-2");

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await ResendAsync(http, broker.ConnectionString, "resend-keep/$DeadLetterQueue", [seq], keepMessageId: true);

        var receiver = client.CreateReceiver("resend-keep");
        var copy = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        copy.ShouldNotBeNull();
        copy.MessageId.ShouldBe("orig-2");
        await receiver.CompleteMessageAsync(copy);
    }

    [Fact]
    public async Task Resend_SessionMessage_IsReceivableViaItsSessionAgain()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(broker.ConnectionString);
        await admin.CreateQueueAsync(new CreateQueueOptions("resend-session") { RequiresSession = true });
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await client.CreateSender("resend-session").SendMessageAsync(
            new ServiceBusMessage("session payload") { MessageId = "sess-orig", SessionId = "s-1" });

        var session = await client.AcceptSessionAsync("resend-session", "s-1");
        var msg = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        await session.DeadLetterMessageAsync(msg, "boom");
        await session.CloseAsync();

        var dlq = client.CreateReceiver("resend-session", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var seq = (await dlq.PeekMessagesAsync(10)).Single(m => m.MessageId == "sess-orig").SequenceNumber;
        await dlq.CloseAsync();

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        var result = await ResendAsync(http, broker.ConnectionString, "resend-session/$DeadLetterQueue", [seq]);
        result["succeeded"]!.GetValue<int>().ShouldBe(1);

        var reopened = await client.AcceptSessionAsync("resend-session", "s-1");
        var copy = await reopened.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        copy.ShouldNotBeNull("the copy must carry SessionId s-1 and land in that session");
        copy.SessionId.ShouldBe("s-1");
        copy.Body.ToString().ShouldBe("session payload");
        await reopened.CompleteMessageAsync(copy);
        await reopened.CloseAsync();
    }

    [Fact]
    public async Task Resend_KeepIdOnDedupQueue_IsSilentlyDroppedInsideTheWindow()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(broker.ConnectionString);
        await admin.CreateQueueAsync(new CreateQueueOptions("resend-dedup")
        {
            RequiresDuplicateDetection = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
        });
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await client.CreateSender("resend-dedup").SendMessageAsync(new ServiceBusMessage("x") { MessageId = "dup-1" });
        var seq = await DeadLetterAndGetSequenceAsync(client, "resend-dedup", "dup-1");

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var kept = await ResendAsync(http, broker.ConnectionString, "resend-dedup/$DeadLetterQueue", [seq], keepMessageId: true);
        kept["succeeded"]!.GetValue<int>().ShouldBe(1, "the send itself is accepted; dedup drops it broker-side");
        var receiver = client.CreateReceiver("resend-dedup");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "a kept MessageId inside the dedup window is dropped by duplicate detection");

        await ResendAsync(http, broker.ConnectionString, "resend-dedup/$DeadLetterQueue", [seq]);
        var copy = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        copy.ShouldNotBeNull("a fresh MessageId passes duplicate detection");
        copy.MessageId.ShouldNotBe("dup-1");
        await receiver.CompleteMessageAsync(copy);
    }

    [Fact]
    public async Task Resend_FromSubscriptionDlq_GoesThroughTheTopicAndFansOutAgain()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        var admin = new ServiceBusAdministrationClient(broker.ConnectionString);
        await admin.CreateTopicAsync("resend-t");
        await admin.CreateSubscriptionAsync("resend-t", "a");
        await admin.CreateSubscriptionAsync("resend-t", "b");
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await client.CreateSender("resend-t").SendMessageAsync(new ServiceBusMessage("fanout") { MessageId = "top-1" });

        foreach (var sub in new[] { "a", "b" })
        {
            var r = client.CreateReceiver("resend-t", sub);
            var m = await r.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            m.ShouldNotBeNull();
            if (sub == "a") await r.DeadLetterMessageAsync(m, "boom");
            else await r.CompleteMessageAsync(m);
            await r.CloseAsync();
        }
        var dlq = client.CreateReceiver("resend-t", "a", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var seq = (await dlq.PeekMessagesAsync(10)).Single().SequenceNumber;
        await dlq.CloseAsync();

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        var result = await ResendAsync(http, broker.ConnectionString, "resend-t/Subscriptions/a/$DeadLetterQueue", [seq]);
        result["destination"]!.GetValue<string>().ShouldBe("resend-t");

        foreach (var sub in new[] { "a", "b" })
        {
            var r = client.CreateReceiver("resend-t", sub);
            var copy = await r.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            copy.ShouldNotBeNull($"the resent copy must fan out to subscription '{sub}' again");
            copy.Body.ToString().ShouldBe("fanout");
            await r.CompleteMessageAsync(copy);
            await r.CloseAsync();
        }
    }

    [Fact]
    public async Task Resend_ToAnExplicitDestination_OverridesTheDefault()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("resend-src");
        await broker.CreateQueueAsync("resend-elsewhere");
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await client.CreateSender("resend-src").SendMessageAsync(new ServiceBusMessage("x") { MessageId = "mv-1" });
        var seq = await DeadLetterAndGetSequenceAsync(client, "resend-src", "mv-1");

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        var result = await ResendAsync(http, broker.ConnectionString, "resend-src/$DeadLetterQueue", [seq],
            destination: "resend-elsewhere");
        result["destination"]!.GetValue<string>().ShouldBe("resend-elsewhere");

        var receiver = client.CreateReceiver("resend-elsewhere");
        var copy = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        copy.ShouldNotBeNull();
        await receiver.CompleteMessageAsync(copy);
    }

    [Fact]
    public async Task Resend_UnknownSequenceNumber_FailsThatItemWithoutAbortingTheRest()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("resend-miss");
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await client.CreateSender("resend-miss").SendMessageAsync(new ServiceBusMessage("x") { MessageId = "hit-1" });
        var seq = await DeadLetterAndGetSequenceAsync(client, "resend-miss", "hit-1");

        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        var result = await ResendAsync(http, broker.ConnectionString, "resend-miss/$DeadLetterQueue", [seq, 999_999]);

        result["succeeded"]!.GetValue<int>().ShouldBe(1);
        result["failed"]!.GetValue<int>().ShouldBe(1);
        var failedItem = result["results"]!.AsArray().Single(r => !r!["ok"]!.GetValue<bool>())!;
        failedItem["sequenceNumber"]!.GetValue<long>().ShouldBe(999_999);
        failedItem["error"]!.GetValue<string>().ShouldContain("not found");
    }

    [Fact]
    public async Task Resend_FromANonDlqAddressWithoutDestination_Returns400()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/resend", new
        {
            connectionString = broker.ConnectionString,
            queue = "plain-queue",
            sequenceNumbers = new[] { 1L },
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
