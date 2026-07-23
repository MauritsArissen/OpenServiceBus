using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.Explorer.Metrics;

/// <summary>
/// Background sampler that periodically snapshots every entity's active message count from
/// the broker's management API and keeps a rolling in-memory time series (24h). This is the
/// only source of historical data the Explorer has - the broker's REST API exposes current
/// counts but no history and no cumulative in/out totals - so the Explorer collects it here.
///
/// One management URL is learned per <c>/api/ping</c>; the collector then polls it on a fixed
/// interval regardless of whether anyone is looking, so the 24h windows fill in over time.
/// State is process-lifetime only (lost on Explorer restart) - acceptable for a dev tool.
/// </summary>
public sealed class MetricsCollector : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MetricsCollector> _logger;
    private readonly ConcurrentDictionary<string, byte> _managementUrls = new();
    private readonly ConcurrentDictionary<string, List<Sample>> _series = new(StringComparer.OrdinalIgnoreCase);

    public MetricsCollector(IHttpClientFactory httpFactory, ILogger<MetricsCollector> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>One snapshot: current depth (<c>Active</c>) plus cumulative lifetime counters.</summary>
    public readonly record struct Sample(long T, long Active, long Enqueued, long Completed);

    /// <summary>Learn a management URL to poll. Idempotent.</summary>
    public void RegisterManagementUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) _managementUrls.TryAdd(url.TrimEnd('/'), 0);
    }

    /// <summary>Samples for an entity address at or after <paramref name="sinceUnix"/>.</summary>
    public IReadOnlyList<Sample> Series(string entity, long sinceUnix)
    {
        if (!_series.TryGetValue(entity, out var list)) return [];
        lock (list) return list.Where(s => s.T >= sinceUnix).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await SampleAllAsync(stoppingToken).ConfigureAwait(false);
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SampleAllAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var baseUrl in _managementUrls.Keys)
        {
            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                // /queues/ returns EVERY backing queue - main queues, subscription queues,
                // and all their /$DeadLetterQueue siblings - each with its active count.
                var rows = await http.GetFromJsonAsync<List<QueueRow>>(baseUrl + "/queues/", ct).ConfigureAwait(false);
                foreach (var row in rows ?? [])
                {
                    if (!string.IsNullOrEmpty(row.Name))
                    {
                        Record(row.Name, now, row.ActiveMessageCount ?? 0, row.EnqueuedCount ?? 0, row.CompletedCount ?? 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metrics sample from {Url} failed.", baseUrl);
            }
        }
    }

    private void Record(string entity, long t, long active, long enqueued, long completed)
    {
        var list = _series.GetOrAdd(entity, _ => new List<Sample>());
        lock (list)
        {
            list.Add(new Sample(t, active, enqueued, completed));
            var cutoff = t - (long)Retention.TotalSeconds;
            if (list.Count > 0 && list[0].T < cutoff) list.RemoveAll(s => s.T < cutoff);
        }
    }

    private sealed record QueueRow(string Name, long? ActiveMessageCount, long? EnqueuedCount, long? CompletedCount);
}
