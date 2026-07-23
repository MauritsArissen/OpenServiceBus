using System.Net.Http.Json;

namespace OpenServiceBus.DemoSeeder;

/// <summary>
/// The fixed set of entities the live demo shows: 2 queues, 1 topic with 3 subscriptions
/// (two of them SQL-filtered). Created via the broker's management REST API. Deleting and
/// recreating this is the "reset".
/// </summary>
public sealed class DemoTopology
{
    public const string TopicName = "events";
    public static readonly string[] Queues = ["orders", "payments"];

    // (subscription, sqlFilter | null). "all" is a catch-all; the other two filter.
    public static readonly (string Name, string? Filter)[] Subscriptions =
    [
        ("all", null),
        ("eu-priority", "region = 'eu' AND priority >= 5"),
        ("errors", "level = 'error' OR eventType = 'payment.failed'"),
    ];

    private readonly HttpClient _http;
    private readonly string _mgmt;

    public DemoTopology(HttpClient http, string managementUrl)
    {
        _http = http;
        _mgmt = managementUrl.TrimEnd('/');
    }

    /// <summary>Delete everything, then recreate the demo entities from scratch.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        await DeleteAllAsync(ct);
        await EnsureAsync(ct);
    }

    /// <summary>Create the demo entities if missing (idempotent - safe on startup).</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        foreach (var q in Queues)
        {
            await _http.PutAsJsonAsync($"{_mgmt}/queues/{q}", new { maxDeliveryCount = 5, lockDuration = "00:00:30" }, ct);
        }
        await _http.PutAsJsonAsync($"{_mgmt}/topics/{TopicName}", new { }, ct);
        foreach (var (name, filter) in Subscriptions)
        {
            await _http.PutAsJsonAsync($"{_mgmt}/topics/{TopicName}/subscriptions/{name}", new { maxDeliveryCount = 5 }, ct);
            if (filter is not null)
            {
                // Replace the auto-created $Default TrueFilter with a SQL rule (same as Azure).
                await _http.PutAsJsonAsync(
                    $"{_mgmt}/topics/{TopicName}/subscriptions/{name}/rules/$Default",
                    new { filterType = "sql", sqlExpression = filter }, ct);
            }
        }
    }

    private async Task DeleteAllAsync(CancellationToken ct)
    {
        try
        {
            var queues = await _http.GetFromJsonAsync<List<Entity>>($"{_mgmt}/queues/", ct) ?? [];
            // Delete only top-level queues (the broker removes their DLQ/subscription siblings).
            foreach (var q in queues.Where(q => !q.Name.Contains("/") && !q.Name.EndsWith("/$DeadLetterQueue")))
            {
                await _http.DeleteAsync($"{_mgmt}/queues/{q.Name}", ct);
            }
            var topics = await _http.GetFromJsonAsync<List<Entity>>($"{_mgmt}/topics/", ct) ?? [];
            foreach (var t in topics)
            {
                await _http.DeleteAsync($"{_mgmt}/topics/{t.Name}", ct);
            }
        }
        catch
        {
            /* best-effort wipe; EnsureAsync recreates regardless */
        }
    }

    private sealed record Entity(string Name);
}
