using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenServiceBus.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// The Explorer's Postman-style environments: CRUD, Postman-shape import/export, reset,
/// and {{name}} resolution on the send path - including the namespace split with the
/// {{$...}} dynamic variables and per-copy resolution of dynamic variables INSIDE
/// environment values.
/// </summary>
public class EnvironmentsEndpointTests
{
    private static object Alice => new
    {
        name = "Card of Alice",
        values = new object[]
        {
            new { key = "cardnumber", value = "123400000", enabled = true },
            new { key = "cardholder", value = "alice", enabled = true },
            new { key = "disabledKey", value = "should-not-resolve", enabled = false },
        },
    };

    [Fact]
    public async Task Crud_CreateListUpdateDelete_RoundTrips()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        (await http.PostAsJsonAsync("/api/environments", Alice)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await http.PostAsJsonAsync("/api/environments", Alice)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var list = JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray();
        list.Count.ShouldBe(1);
        list[0]!["name"]!.GetValue<string>().ShouldBe("Card of Alice");
        list[0]!["values"]!.AsArray().Count.ShouldBe(3);

        var update = await http.PutAsJsonAsync("/api/environments/Card of Alice", new
        {
            name = "Card of Alice",
            values = new object[] { new { key = "cardnumber", value = "999", enabled = true } },
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray()[0]!["values"]!.AsArray().Count.ShouldBe(1);

        (await http.DeleteAsync("/api/environments/Card of Alice")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await http.DeleteAsync("/api/environments/Card of Alice")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Import_AcceptsThePostmanExportShape_IgnoringExtraFields()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();

        // A real Postman environment export carries id/_postman_variable_scope/type fields.
        var postmanShaped = JsonNode.Parse("""
            [{
              "id": "8b6be6a2-1111-2222-3333-444455556666",
              "name": "From Postman",
              "values": [
                { "key": "host", "value": "orders.internal", "type": "default", "enabled": true },
                { "key": "token", "value": "abc", "type": "secret", "enabled": false }
              ],
              "_postman_variable_scope": "environment",
              "_postman_exported_at": "2026-08-24T10:00:00.000Z"
            }]
            """);

        var import = await http.PostAsJsonAsync("/api/environments/import", new { environments = postmanShaped });

        import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());
        var list = JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray();
        list[0]!["name"]!.GetValue<string>().ShouldBe("From Postman");
        var values = list[0]!["values"]!.AsArray();
        values.Count.ShouldBe(2);
        values[0]!["key"]!.GetValue<string>().ShouldBe("host");
        values[1]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Import_ReportsConflictsUntilAModeIsChosen()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/environments", Alice);

        var probe = await http.PostAsJsonAsync("/api/environments/import", new { environments = new[] { Alice } });
        probe.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonNode.Parse(await probe.Content.ReadAsStringAsync())!["conflicts"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ShouldBe(["Card of Alice"]);

        var replace = await http.PostAsJsonAsync("/api/environments/import", new
        {
            environments = new[] { Alice },
            conflictMode = "replace",
        });
        replace.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await replace.Content.ReadAsStringAsync())!["replaced"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Export_RoundTripsThroughImport()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/environments", Alice);

        var response = await http.GetAsync("/api/environments/export");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("environments.json");
        var exported = await response.Content.ReadAsStringAsync();

        await using var fresh = new WebApplicationFactory<Program>();
        using var freshHttp = fresh.CreateClient();
        var import = await freshHttp.PostAsJsonAsync("/api/environments/import", new
        {
            environments = JsonNode.Parse(exported),
            conflictMode = "replace",
        });
        import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());
        var list = JsonNode.Parse(await freshHttp.GetStringAsync("/api/environments"))!.AsArray();
        list[0]!["name"]!.GetValue<string>().ShouldBe("Card of Alice");
        list[0]!["values"]!.AsArray().Count.ShouldBe(3);
    }

    [Fact]
    public async Task Duplicate_And_Reset_BehaveLikeTheCannedLibrary()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/environments", Alice);

        var copy = JsonNode.Parse(await (await http.PostAsync("/api/environments/Card of Alice/duplicate", null)).Content.ReadAsStringAsync())!;
        copy["name"]!.GetValue<string>().ShouldBe("Card of Alice (copy)");

        (await http.PostAsync("/api/environments/reset", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Send_WithAnActiveEnvironment_ResolvesNamesAndKeepsTheNamespaceSplit()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("env-send");
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/environments", Alice);

        var resp = await http.PostAsJsonAsync("/api/send", new
        {
            connectionString = broker.ConnectionString,
            queue = "env-send",
            body = "{\"cardnr\": {{cardnumber}}, \"cardholder\": \"{{cardholder}}\", \"txn\": \"{{$guid}}\", \"missing\": \"{{unknownKey}}\", \"off\": \"{{disabledKey}}\"}",
            subject = "charge for {{cardholder}}",
            environment = "Card of Alice",
            count = 1,
            strategy = "ATONCE",
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("env-send");
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        msg.ShouldNotBeNull();
        var body = msg.Body.ToString();
        body.ShouldContain("\"cardnr\": 123400000");
        body.ShouldContain("\"cardholder\": \"alice\"");
        body.ShouldNotContain("{{$guid}}");
        body.ShouldContain("\"missing\": \"{{unknownKey}}\"", customMessage: "unresolved names stay verbatim");
        body.ShouldContain("\"off\": \"{{disabledKey}}\"", customMessage: "disabled values must not resolve");
        msg.Subject.ShouldBe("charge for alice");
        await receiver.CompleteMessageAsync(msg);
    }

    [Fact]
    public async Task Send_EnvironmentValueContainingADynamicVariable_ResolvesPerCopy()
    {
        await using var broker = await OpenServiceBusTestHost.StartAsync();
        await broker.CreateQueueAsync("env-dyn");
        await using var factory = new WebApplicationFactory<Program>();
        using var http = factory.CreateClient();
        await http.PostAsJsonAsync("/api/environments", new
        {
            name = "generator",
            values = new object[] { new { key = "traceId", value = "trace-{{$guid}}", enabled = true } },
        });

        var resp = await http.PostAsJsonAsync("/api/send", new
        {
            connectionString = broker.ConnectionString,
            queue = "env-dyn",
            body = "{{traceId}}",
            environment = "generator",
            count = 3,
            strategy = "ATONCE",
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var client = new ServiceBusClient(broker.ConnectionString);
        var receiver = client.CreateReceiver("env-dyn");
        var received = await receiver.ReceiveMessagesAsync(3, TimeSpan.FromSeconds(10));
        received.Count.ShouldBe(3);
        var bodies = received.Select(m => m.Body.ToString()).ToList();
        bodies.ShouldAllBe(b => b.StartsWith("trace-") && !b.Contains("{{"));
        bodies.Distinct().Count().ShouldBe(3, "the dynamic variable inside the environment value must resolve per copy");
        foreach (var m in received) await receiver.CompleteMessageAsync(m);
    }
}
