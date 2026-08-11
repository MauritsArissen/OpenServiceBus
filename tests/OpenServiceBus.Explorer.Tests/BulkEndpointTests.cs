using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenServiceBus.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// The Explorer's POST /api/bulk against a real broker: many tracked locks settled in one
/// request, per-token results, and no batch-wide abort when a single token fails.
/// </summary>
public class BulkEndpointTests
{
    private static async Task SendAsync(OpenServiceBusTestHost broker, string queue, int count)
    {
        await using var client = new ServiceBusClient(broker.ConnectionString);
        await using var sender = client.CreateSender(queue);
        for (var i = 0; i < count; i++)
        {
            await sender.SendMessageAsync(new ServiceBusMessage($"payload-{i}") { MessageId = $"m-{i}" });
        }
    }

    private static async Task<List<(string Token, long Seq)>> LockAllAsync(HttpClient http, string conn, string queue, int count)
    {
        var locks = new List<(string, long)>();
        for (var i = 0; i < count; i++)
        {
            var resp = await http.PostAsJsonAsync("/api/receive", new { connectionString = conn, queue, timeoutSeconds = 10 });
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            node["received"]!.GetValue<bool>().ShouldBeTrue($"expected to lock message {i + 1}/{count} from {queue}");
            locks.Add((node["lockToken"]!.GetValue<string>(), node["sequenceNumber"]!.GetValue<long>()));
        }
        return locks;
    }

    private static async Task<JsonNode> BulkAsync(
        HttpClient http, string conn, string queue, string action, IEnumerable<string> tokens,
        string? reason = null, string? description = null)
    {
        var resp = await http.PostAsJsonAsync("/api/bulk", new
        {
            connectionString = conn,
            queue,
            action,
            lockTokens = tokens.ToArray(),
            reason,
            description,
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task BulkComplete_SettlesEverySelectedMessage()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("bulk-complete");
        await SendAsync(broker, "bulk-complete", 5);
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var locks = await LockAllAsync(http, broker.ConnectionString, "bulk-complete", 5);
        var result = await BulkAsync(http, broker.ConnectionString, "bulk-complete", "complete", locks.Select(l => l.Token));

        result["succeeded"]!.GetValue<int>().ShouldBe(5);
        result["failed"]!.GetValue<int>().ShouldBe(0);

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("bulk-complete");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "every message should have been completed off the queue");
    }

    [Fact]
    public async Task BulkAbandon_IncrementsDeliveryCountOnEverySelectedMessage()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("bulk-abandon");
        await SendAsync(broker, "bulk-abandon", 2);
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var locks = await LockAllAsync(http, broker.ConnectionString, "bulk-abandon", 2);
        var result = await BulkAsync(http, broker.ConnectionString, "bulk-abandon", "abandon", locks.Select(l => l.Token));
        result["succeeded"]!.GetValue<int>().ShouldBe(2);

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("bulk-abandon");
        for (var i = 0; i < 2; i++)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            msg.ShouldNotBeNull();
            msg.DeliveryCount.ShouldBe(2);
            await receiver.CompleteMessageAsync(msg);
        }
    }

    [Fact]
    public async Task BulkDeadLetter_WithSharedReason_LandsEverySelectedMessageInTheDlq()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("bulk-dlq");
        await SendAsync(broker, "bulk-dlq", 3);
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var locks = await LockAllAsync(http, broker.ConnectionString, "bulk-dlq", 3);
        var result = await BulkAsync(
            http, broker.ConnectionString, "bulk-dlq", "deadletter", locks.Select(l => l.Token),
            reason: "bulk-test", description: "poisoned batch");
        result["succeeded"]!.GetValue<int>().ShouldBe(3);

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var dlqReceiver = client.CreateReceiver("bulk-dlq", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        for (var i = 0; i < 3; i++)
        {
            var msg = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            msg.ShouldNotBeNull();
            msg.DeadLetterReason.ShouldBe("bulk-test");
            msg.DeadLetterErrorDescription.ShouldBe("poisoned batch");
            await dlqReceiver.CompleteMessageAsync(msg);
        }
    }

    [Fact]
    public async Task BulkDefer_DefersEverySelectedMessage()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("bulk-defer");
        await SendAsync(broker, "bulk-defer", 2);
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var locks = await LockAllAsync(http, broker.ConnectionString, "bulk-defer", 2);
        var result = await BulkAsync(http, broker.ConnectionString, "bulk-defer", "defer", locks.Select(l => l.Token));
        result["succeeded"]!.GetValue<int>().ShouldBe(2);

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("bulk-defer");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "deferred messages must not be received by the regular receive path");
        var deferred = await receiver.ReceiveDeferredMessagesAsync(locks.Select(l => l.Seq).ToArray());
        deferred.Count.ShouldBe(2);
        foreach (var msg in deferred)
        {
            await receiver.CompleteMessageAsync(msg);
        }
    }

    [Fact]
    public async Task BulkRequeue_FromTheDlq_ReturnsEverySelectedMessageToTheParentQueue()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("bulk-requeue");
        await SendAsync(broker, "bulk-requeue", 2);
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var locks = await LockAllAsync(http, broker.ConnectionString, "bulk-requeue", 2);
        await BulkAsync(http, broker.ConnectionString, "bulk-requeue", "deadletter", locks.Select(l => l.Token), reason: "poison");

        var dlqLocks = await LockAllAsync(http, broker.ConnectionString, "bulk-requeue/$DeadLetterQueue", 2);
        var result = await BulkAsync(http, broker.ConnectionString, "bulk-requeue/$DeadLetterQueue", "requeue", dlqLocks.Select(l => l.Token));
        result["succeeded"]!.GetValue<int>().ShouldBe(2);

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("bulk-requeue");
        for (var i = 0; i < 2; i++)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
            msg.ShouldNotBeNull();
            msg.ApplicationProperties.ContainsKey("DeadLetterReason").ShouldBeFalse(
                "requeued copies must start clean, without broker-stamped DLQ markers");
            await receiver.CompleteMessageAsync(msg);
        }
        var dlqReceiver = client.CreateReceiver("bulk-requeue", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        (await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "the DLQ originals should have been completed by the requeue");
    }

    [Fact]
    public async Task Bulk_UnknownToken_FailsThatTokenWithoutAbortingTheRest()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("bulk-partial");
        await SendAsync(broker, "bulk-partial", 2);
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var locks = await LockAllAsync(http, broker.ConnectionString, "bulk-partial", 2);
        var tokens = new[] { locks[0].Token, "not-a-real-token", locks[1].Token };
        var result = await BulkAsync(http, broker.ConnectionString, "bulk-partial", "complete", tokens);

        result["succeeded"]!.GetValue<int>().ShouldBe(2);
        result["failed"]!.GetValue<int>().ShouldBe(1);
        var results = result["results"]!.AsArray();
        results.Count.ShouldBe(3);
        var failed = results.Single(r => !r!["ok"]!.GetValue<bool>())!;
        failed["lockToken"]!.GetValue<string>().ShouldBe("not-a-real-token");
        failed["lockLost"]!.GetValue<bool>().ShouldBeTrue();
        failed["error"]!.GetValue<string>().ShouldContain("Unknown lock token");

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("bulk-partial");
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "the two valid tokens must still have been completed");
    }

    [Theory]
    [InlineData("explode")]
    [InlineData("")]
    public async Task Bulk_UnknownAction_Returns400(string action)
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/bulk", new
        {
            connectionString = broker.ConnectionString,
            queue = "whatever",
            action,
            lockTokens = new[] { "t" },
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bulk_EmptyTokenList_Returns400()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/bulk", new
        {
            connectionString = broker.ConnectionString,
            queue = "whatever",
            action = "complete",
            lockTokens = Array.Empty<string>(),
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
