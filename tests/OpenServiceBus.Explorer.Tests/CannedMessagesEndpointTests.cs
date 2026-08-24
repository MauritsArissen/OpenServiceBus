using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenServiceBus.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// The Explorer's canned message library API: CRUD, duplicate, import with conflict
/// handling, reset to defaults, and dynamic variable resolution on the send path.
/// </summary>
public class CannedMessagesEndpointTests
{
    private static object Canned(string name, string? body = null, string? target = null) => new
    {
        name,
        targetEntity = target,
        body = body ?? $"body of {name}",
        contentType = "text/plain",
        count = 1,
        strategy = "ATONCE",
    };

    [Fact]
    public async Task Crud_CreateListUpdateDelete_RoundTrips()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        (await http.PostAsJsonAsync("/api/canned", Canned("order-created"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await http.PostAsJsonAsync("/api/canned", Canned("order-created"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var list = JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray();
        list.Count.ShouldBe(1);
        list[0]!["name"]!.GetValue<string>().ShouldBe("order-created");

        var update = await http.PutAsJsonAsync("/api/canned/order-created", Canned("order-created", body: "v2"));
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray()[0]!["body"]!.GetValue<string>().ShouldBe("v2");

        var rename = await http.PutAsJsonAsync("/api/canned/order-created", Canned("order-updated"));
        rename.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await http.DeleteAsync("/api/canned/order-updated")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await http.DeleteAsync("/api/canned/order-updated")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Rename_OntoAnExistingName_Conflicts()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/canned", Canned("a"));
        await http.PostAsJsonAsync("/api/canned", Canned("b"));

        var rename = await http.PutAsJsonAsync("/api/canned/a", Canned("b"));

        rename.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_WithoutAName_IsRejected()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        (await http.PostAsJsonAsync("/api/canned", Canned(""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync("/api/canned", Canned("   "))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Duplicate_PicksTheNextFreeCopyName()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/canned", Canned("template"));

        var first = JsonNode.Parse(await (await http.PostAsync("/api/canned/template/duplicate", null)).Content.ReadAsStringAsync())!;
        var second = JsonNode.Parse(await (await http.PostAsync("/api/canned/template/duplicate", null)).Content.ReadAsStringAsync())!;

        first["name"]!.GetValue<string>().ShouldBe("template (copy)");
        second["name"]!.GetValue<string>().ShouldBe("template (copy 2)");
        first["body"]!.GetValue<string>().ShouldBe("body of template");
    }

    [Fact]
    public async Task Import_ReportsConflictsUntilAModeIsChosen()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/canned", Canned("existing", body: "original"));

        var probe = await http.PostAsJsonAsync("/api/canned/import", new
        {
            messages = new[] { Canned("existing", body: "incoming"), Canned("fresh") },
        });
        probe.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonNode.Parse(await probe.Content.ReadAsStringAsync())!["conflicts"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ShouldBe(["existing"]);

        var skip = await http.PostAsJsonAsync("/api/canned/import", new
        {
            messages = new[] { Canned("existing", body: "incoming"), Canned("fresh") },
            conflictMode = "skip",
        });
        skip.StatusCode.ShouldBe(HttpStatusCode.OK);
        var skipSummary = JsonNode.Parse(await skip.Content.ReadAsStringAsync())!;
        skipSummary["added"]!.GetValue<int>().ShouldBe(1);
        skipSummary["skipped"]!.GetValue<int>().ShouldBe(1);

        var replace = await http.PostAsJsonAsync("/api/canned/import", new
        {
            messages = new[] { Canned("existing", body: "incoming") },
            conflictMode = "replace",
        });
        replace.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await replace.Content.ReadAsStringAsync())!["replaced"]!.GetValue<int>().ShouldBe(1);

        var list = JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray();
        list.First(n => n!["name"]!.GetValue<string>() == "existing")!["body"]!.GetValue<string>().ShouldBe("incoming");
    }

    [Fact]
    public async Task Reset_RestoresTheDefaultLibrary()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/canned", Canned("visitor-added"));

        var reset = await http.PostAsync("/api/canned/reset", null);

        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Send_IndexAndSequence_ResolvePerCopyThroughTheSendPath()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("catalogue-send");
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/send", new
        {
            connectionString = broker.ConnectionString,
            queue = "catalogue-send",
            body = "copy={{$index}} seq={{$sequence 500}} ulid={{$ulid}}",
            count = 3,
            strategy = "ATONCE",
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("catalogue-send");
        var received = await receiver.ReceiveMessagesAsync(3, TimeSpan.FromSeconds(10));
        received.Count.ShouldBe(3);

        var bodies = received.Select(m => m.Body.ToString()).OrderBy(b => b).ToList();
        bodies.Select(b => b.Split(' ')[0]).ShouldBe(["copy=0", "copy=1", "copy=2"]);
        bodies.Select(b => b.Split(' ')[1]).Distinct().Count().ShouldBe(3, "each copy gets its own sequence value");
        bodies.Select(b => b.Split(' ')[1][4..]).Select(long.Parse).Min().ShouldBe(500);
        bodies.Select(b => b.Split(' ')[2][5..]).ShouldAllBe(u => u.Length == 26);
        foreach (var m in received) await receiver.CompleteMessageAsync(m);
    }

    [Fact]
    public async Task Send_WithDynamicVariables_ResolvesPerCopy()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("canned-send");
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/send", new
        {
            connectionString = broker.ConnectionString,
            queue = "canned-send",
            body = "{\"id\": \"{{$guid upper}}\", \"at\": \"{{$datetime iso8601}}\"}",
            messageId = "cm-{{$guid}}",
            subject = "batch {{$datetime rfc1123}}",
            count = 3,
            strategy = "ATONCE",
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("canned-send");
        var received = await receiver.ReceiveMessagesAsync(3, TimeSpan.FromSeconds(10));
        received.Count.ShouldBe(3);

        var bodies = received.Select(m => m.Body.ToString()).ToList();
        bodies.Distinct().Count().ShouldBe(3, "each copy must resolve its own {{$guid}}");
        bodies.ShouldAllBe(b => !b.Contains("{{$"));
        received.Select(m => m.MessageId).Distinct().Count().ShouldBe(3);
        received.ShouldAllBe(m => m.MessageId.StartsWith("cm-") && !m.MessageId.Contains("{{$") && !m.MessageId.EndsWith("-0"));
        received.ShouldAllBe(m => m.Subject.StartsWith("batch ") && m.Subject.Contains("GMT"));
        foreach (var m in received) await receiver.CompleteMessageAsync(m);
    }
}
