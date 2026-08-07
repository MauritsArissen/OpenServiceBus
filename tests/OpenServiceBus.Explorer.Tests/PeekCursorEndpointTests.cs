using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenServiceBus.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// POST /api/peek with fromSequenceNumber: the client-owned peek cursor pages through a
/// queue the way the real Service Bus SDKs do (continue from last sequence number + 1).
/// </summary>
public class PeekCursorEndpointTests
{
    private static async Task<JsonNode> PeekAsync(HttpClient http, string conn, string queue, int max, long from)
    {
        var resp = await http.PostAsJsonAsync("/api/peek", new
        {
            connectionString = conn,
            queue,
            maxMessages = max,
            fromSequenceNumber = from,
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task Peek_WithFromSequenceNumber_PagesThroughTheWholeQueue()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("peek-pages");
        await using (var client = new ServiceBusClient(broker.ConnectionString))
        {
            await using var sender = client.CreateSender("peek-pages");
            for (var i = 0; i < 5; i++)
            {
                await sender.SendMessageAsync(new ServiceBusMessage($"page-{i}") { MessageId = $"p-{i}" });
            }
        }
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var seen = new List<long>();
        var from = 0L;
        while (true)
        {
            var page = await PeekAsync(http, broker.ConnectionString, "peek-pages", 2, from);
            var count = page["count"]!.GetValue<int>();
            if (count == 0) break;
            count.ShouldBeLessThanOrEqualTo(2);
            var seqs = page["messages"]!.AsArray().Select(m => m!["sequenceNumber"]!.GetValue<long>()).ToList();
            seqs.ShouldAllBe(s => s >= from, "a page must only contain sequence numbers at or past the cursor");
            seen.AddRange(seqs);
            from = seqs.Max() + 1;
        }

        seen.Count.ShouldBe(5);
        seen.ShouldBe(seen.OrderBy(s => s).ToList(), "pages must arrive in enqueue order");
        seen.Distinct().Count().ShouldBe(5, "no message may appear on two pages");
    }

    [Fact]
    public async Task Peek_WithoutFromSequenceNumber_StillAnchorsAtTheHead()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("peek-head");
        await using (var client = new ServiceBusClient(broker.ConnectionString))
        {
            await using var sender = client.CreateSender("peek-head");
            await sender.SendMessageAsync(new ServiceBusMessage("first") { MessageId = "h-0" });
            await sender.SendMessageAsync(new ServiceBusMessage("second") { MessageId = "h-1" });
        }
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/peek", new
        {
            connectionString = broker.ConnectionString,
            queue = "peek-head",
            maxMessages = 1,
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        first["count"]!.GetValue<int>().ShouldBe(1);
        first["messages"]![0]!["messageId"]!.GetValue<string>().ShouldBe("h-0");

        var again = await PeekAsync(http, broker.ConnectionString, "peek-head", 1, 0);
        again["messages"]![0]!["messageId"]!.GetValue<string>().ShouldBe("h-0",
            "an explicit fromSequenceNumber of 0 restarts from the head");
    }

    [Fact]
    public async Task Peek_PastTheEnd_ReturnsAnEmptyPage()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("peek-end");
        await using (var client = new ServiceBusClient(broker.ConnectionString))
        {
            await using var sender = client.CreateSender("peek-end");
            await sender.SendMessageAsync(new ServiceBusMessage("only") { MessageId = "e-0" });
        }
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var page = await PeekAsync(http, broker.ConnectionString, "peek-end", 10, 0);
        page["count"]!.GetValue<int>().ShouldBe(1);
        var last = page["messages"]![0]!["sequenceNumber"]!.GetValue<long>();

        var empty = await PeekAsync(http, broker.ConnectionString, "peek-end", 10, last + 1);
        empty["count"]!.GetValue<int>().ShouldBe(0);
    }
}
