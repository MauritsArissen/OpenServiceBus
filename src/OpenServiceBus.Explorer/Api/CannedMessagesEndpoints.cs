using Azure.Core;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc;
using OpenServiceBus.Explorer.Metrics;
using OpenServiceBus.Explorer.Sessions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenServiceBus.Explorer.Api;

public static class CannedMessagesEndpoints
{
    // In the hosted live demo the broker addresses are fixed by the environment and the UI locks
    // its connection inputs. The proxy endpoints must ignore any client-supplied broker address in
    // that mode, otherwise the backend is an open proxy an anonymous caller can point at internal
    // hosts (SSRF). Outside demo mode the Explorer is a local dev tool, so client control is kept.
    private static readonly bool DemoMode =
        string.Equals(Environment.GetEnvironmentVariable("OSB_EXPLORER_DEMO"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string? PinnedConnectionString = NonEmpty(Environment.GetEnvironmentVariable("OSB_EXPLORER_CONNECTION"));
    private static readonly string? PinnedManagementUrl = NonEmpty(Environment.GetEnvironmentVariable("OSB_EXPLORER_MGMT_URL"));

    private static string? NonEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    private static string ResolveManagementUrl(string? clientValue) =>
        DemoMode && PinnedManagementUrl is not null ? PinnedManagementUrl : (clientValue ?? string.Empty);

    public static IEndpointRouteBuilder MapCannedMessagesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapPost("/list-canned-messages", async (ListCannedMessagesRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var mgmt = ResolveManagementUrl(req.ManagementUrl);
            if (string.IsNullOrWhiteSpace(mgmt))
            {
                return Results.BadRequest(new { error = "Purge is an OpenServiceBus-native operation and needs the broker's management URL." });
            }

            var http = httpFactory.CreateClient();

            var resp = await http.GetAsync(Combine(mgmt, "/canned-messages"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(string.IsNullOrEmpty(body) ? "{}" : body, "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapPost("/create-canned-message", async (CreateCannedMessagesRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var mgmt = ResolveManagementUrl(req.ManagementUrl);
            if (string.IsNullOrWhiteSpace(mgmt))
            {
                return Results.BadRequest(new { error = "Canned messages related API are OpenServiceBus-native operations and needs the broker's management URL." });
            }

            var payload = JsonSerializer.Serialize(new { name = req.Name, topicOrQueue = req.TopicOrQueue, message = req.SendRequest });
            var http = httpFactory.CreateClient();
            var resp = await http.PutAsync(Combine(mgmt, $"/canned-messages/{req.Name}"), content: new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            return Results.Content(string.IsNullOrEmpty(body) ? "{}" : body, "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapPost("/import-canned-messages", async ([AsParameters] ImportCannedMessagesRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (req.File.Length == 0)
            {
                return Results.BadRequest("Empty file.");
            }

            var mgmt = ResolveManagementUrl(req.ManagementUrl);
            if (string.IsNullOrWhiteSpace(mgmt))
            {
                return Results.BadRequest(new { error = "Canned messages related API are OpenServiceBus-native operations and needs the broker's management URL." });
            }

            List<CannedMessageData>? messages;
            try
            {
                await using var stream = req.File.OpenReadStream();
                messages = await JsonSerializer.DeserializeAsync<List<CannedMessageData>>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest($"Invalid JSON: {ex.Message}");
            }

            if (messages is null or { Count: 0 })
            {
                return Results.BadRequest("No canned messages found in file.");
            }

            var http = httpFactory.CreateClient();
            List<string> errors = [];
            int successCount = 0;
            foreach (var message in messages)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new { name = message.Name, topicOrQueue = message.TopicOrQueue, message = message.Message });
                    var resp = await http.PutAsync(Combine(mgmt, $"/canned-messages/{message.Name}"), content: new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    resp.EnsureSuccessStatusCode();
                    successCount++;
                }
                catch (Exception e)
                {
                    errors.Add(e.Message + e.InnerException?.Message);
                }
            }

            var result = new
            {
                imported = successCount,
                success = errors.Count == 0,
                errors = errors.Count > 0 ? errors : null,
            };

            return Results.Content(JsonSerializer.Serialize(result), "application/json");
        })
        .DisableAntiforgery();

        return endpoints;
    }

    private static string Combine(string baseUrl, string suffix)
        => baseUrl.TrimEnd('/') + suffix;
}

public sealed record ListCannedMessagesRequest(string? ManagementUrl);

public sealed record CreateCannedMessagesRequest(string? ManagementUrl, string Name, string TopicOrQueue, SendRequest SendRequest);

public sealed record ImportCannedMessagesRequest([FromForm] string? ManagementUrl, IFormFile File);

public sealed record CannedMessageData
{
    public required string Name { get; init; }

    public required string TopicOrQueue { get; set; }

    public required SendRequest Message { get; set; }
}
