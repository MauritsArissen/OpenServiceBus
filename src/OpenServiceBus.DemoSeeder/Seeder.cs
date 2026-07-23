using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenServiceBus.DemoSeeder;

/// <summary>
/// Drives pretty, fluctuating load against the demo broker: it steers the total active
/// message count along a slow sine wave (never past the cap) by sending or draining, and
/// sprinkles in dead-lettering, DLQ cleanup, defers and abandons so every feature has live
/// data to look at. It also wipes + reseeds the topology on fixed wall-clock boundaries so
/// the demo self-cleans every N minutes.
/// </summary>
public sealed class Seeder : BackgroundService
{
    private readonly SeederOptions _opts;
    private readonly ILogger<Seeder> _logger;

    private static readonly string[] Regions = ["eu", "us", "apac"];
    private static readonly string[] EventTypes = ["order.created", "order.shipped", "payment.completed", "payment.failed"];
    private static readonly string[] Levels = ["info", "info", "info", "warn", "error"];

    public Seeder(SeederOptions opts, ILogger<Seeder> logger)
    {
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var topology = new DemoTopology(http, _opts.ManagementUrl);

        // Wait for the broker to answer, then create the topology.
        await WaitForBrokerAsync(http, ct);
        await topology.EnsureAsync(ct);
        _logger.LogInformation("Demo topology ready. Reset every {Min} min; active cap {Cap}.",
            _opts.ResetIntervalSeconds / 60, _opts.MaxActive);

        var client = new ServiceBusClient(_opts.ConnectionString);
        var senders = new Dictionary<string, ServiceBusSender>();
        var receivers = new Dictionary<string, ServiceBusReceiver>();
        long lastResetBoundary = CurrentBoundary();
        var startedTicks = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Scheduled reset on wall-clock boundaries.
                var boundary = CurrentBoundary();
                if (boundary != lastResetBoundary)
                {
                    lastResetBoundary = boundary;
                    _logger.LogInformation("Resetting demo environment…");
                    foreach (var r in receivers.Values) await r.DisposeAsync();
                    foreach (var s in senders.Values) await s.DisposeAsync();
                    receivers.Clear();
                    senders.Clear();
                    await client.DisposeAsync();
                    await topology.ResetAsync(ct);
                    client = new ServiceBusClient(_opts.ConnectionString);
                }

                await TickAsync(http, client, senders, receivers, startedTicks, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Seeder tick failed; continuing.");
            }

            await Task.Delay(Random.Shared.Next(500, 1000), ct);
        }

        foreach (var r in receivers.Values) await r.DisposeAsync();
        foreach (var s in senders.Values) await s.DisposeAsync();
        await client.DisposeAsync();
    }

    private async Task TickAsync(
        HttpClient http, ServiceBusClient client,
        Dictionary<string, ServiceBusSender> senders, Dictionary<string, ServiceBusReceiver> receivers,
        long startedTicks, CancellationToken ct)
    {
        var active = await TotalActiveAsync(http, ct);
        var elapsed = (Environment.TickCount64 - startedTicks) / 1000.0;

        // Slow sine target between ~[cap*0.15, cap*0.85] over an 8-minute period.
        var mid = _opts.MaxActive * 0.5;
        var amp = _opts.MaxActive * 0.35;
        var target = mid + amp * Math.Sin(2 * Math.PI * elapsed / 480.0);

        if (active < target && active < _opts.MaxActive - 20)
        {
            await SendBurstAsync(client, senders, Random.Shared.Next(3, 12), ct);
        }
        else if (active > target)
        {
            await DrainAsync(client, receivers, Random.Shared.Next(4, 14), ct);
        }

        // Feature sprinkles - low probability each tick so DLQs, defers, etc. stay populated.
        var roll = Random.Shared.NextDouble();
        if (roll < 0.18) await DeadLetterSomeAsync(client, receivers, ct);
        else if (roll < 0.30) await CleanDlqAsync(client, receivers, ct);
        else if (roll < 0.40) await DeferSomeAsync(client, receivers, ct);
        else if (roll < 0.48) await AbandonSomeAsync(client, receivers, ct);
    }

    // ---- operations ----

    private static readonly string[] SendTargets = ["orders", "payments", DemoTopology.TopicName];
    private static string[] SubAddresses =>
        DemoTopology.Subscriptions.Select(s => $"{DemoTopology.TopicName}/Subscriptions/{s.Name}").ToArray();

    private ServiceBusSender Sender(ServiceBusClient client, Dictionary<string, ServiceBusSender> cache, string entity)
    {
        if (!cache.TryGetValue(entity, out var s)) cache[entity] = s = client.CreateSender(entity);
        return s;
    }

    private ServiceBusReceiver Receiver(ServiceBusClient client, Dictionary<string, ServiceBusReceiver> cache, string entity, bool dlq = false)
    {
        var key = dlq ? entity + "|dlq" : entity;
        if (cache.TryGetValue(key, out var r)) return r;
        // Subscription addresses are "<topic>/Subscriptions/<sub>".
        var parts = entity.Split("/Subscriptions/");
        ServiceBusReceiver created = parts.Length == 2
            ? client.CreateReceiver(parts[0], parts[1], new ServiceBusReceiverOptions { SubQueue = dlq ? SubQueue.DeadLetter : SubQueue.None })
            : client.CreateReceiver(entity, new ServiceBusReceiverOptions { SubQueue = dlq ? SubQueue.DeadLetter : SubQueue.None });
        cache[key] = created;
        return created;
    }

    private async Task SendBurstAsync(ServiceBusClient client, Dictionary<string, ServiceBusSender> senders, int n, CancellationToken ct)
    {
        var target = SendTargets[Random.Shared.Next(SendTargets.Length)];
        var sender = Sender(client, senders, target);
        var batch = new List<ServiceBusMessage>(n);
        for (var i = 0; i < n; i++) batch.Add(NewMessage(target == DemoTopology.TopicName));
        await sender.SendMessagesAsync(batch, ct);
    }

    private async Task DrainAsync(ServiceBusClient client, Dictionary<string, ServiceBusReceiver> receivers, int n, CancellationToken ct)
    {
        var entity = ReceivableEntities()[Random.Shared.Next(ReceivableEntities().Length)];
        var receiver = Receiver(client, receivers, entity);
        var msgs = await receiver.ReceiveMessagesAsync(n, TimeSpan.FromMilliseconds(300), ct);
        foreach (var m in msgs) await receiver.CompleteMessageAsync(m, ct);
    }

    private async Task DeadLetterSomeAsync(ServiceBusClient client, Dictionary<string, ServiceBusReceiver> receivers, CancellationToken ct)
    {
        var entity = ReceivableEntities()[Random.Shared.Next(ReceivableEntities().Length)];
        var receiver = Receiver(client, receivers, entity);
        var msgs = await receiver.ReceiveMessagesAsync(Random.Shared.Next(1, 4), TimeSpan.FromMilliseconds(300), ct);
        foreach (var m in msgs)
            await receiver.DeadLetterMessageAsync(m, "demo-dead-letter", "seeded by the demo load generator", ct);
    }

    private async Task CleanDlqAsync(ServiceBusClient client, Dictionary<string, ServiceBusReceiver> receivers, CancellationToken ct)
    {
        var entity = ReceivableEntities()[Random.Shared.Next(ReceivableEntities().Length)];
        var receiver = Receiver(client, receivers, entity, dlq: true);
        var msgs = await receiver.ReceiveMessagesAsync(Random.Shared.Next(1, 5), TimeSpan.FromMilliseconds(300), ct);
        foreach (var m in msgs) await receiver.CompleteMessageAsync(m, ct);
    }

    private async Task DeferSomeAsync(ServiceBusClient client, Dictionary<string, ServiceBusReceiver> receivers, CancellationToken ct)
    {
        var entity = ReceivableEntities()[Random.Shared.Next(ReceivableEntities().Length)];
        var receiver = Receiver(client, receivers, entity);
        var msgs = await receiver.ReceiveMessagesAsync(Random.Shared.Next(1, 3), TimeSpan.FromMilliseconds(300), ct);
        foreach (var m in msgs) await receiver.DeferMessageAsync(m, cancellationToken: ct);
    }

    private async Task AbandonSomeAsync(ServiceBusClient client, Dictionary<string, ServiceBusReceiver> receivers, CancellationToken ct)
    {
        var entity = ReceivableEntities()[Random.Shared.Next(ReceivableEntities().Length)];
        var receiver = Receiver(client, receivers, entity);
        var msgs = await receiver.ReceiveMessagesAsync(Random.Shared.Next(1, 4), TimeSpan.FromMilliseconds(300), ct);
        foreach (var m in msgs) await receiver.AbandonMessageAsync(m, cancellationToken: ct);
    }

    // ---- helpers ----

    private static string[] ReceivableEntities() =>
        [.. DemoTopology.Queues, .. SubAddresses];

    private static ServiceBusMessage NewMessage(bool forTopic)
    {
        var region = Regions[Random.Shared.Next(Regions.Length)];
        var priority = Random.Shared.Next(1, 10);
        var eventType = EventTypes[Random.Shared.Next(EventTypes.Length)];
        var level = Levels[Random.Shared.Next(Levels.Length)];
        var body = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N")[..8], region, priority, eventType, level });
        var msg = new ServiceBusMessage(body)
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Subject = eventType,
            ContentType = "application/json",
        };
        if (forTopic)
        {
            msg.ApplicationProperties["region"] = region;
            msg.ApplicationProperties["priority"] = priority;
            msg.ApplicationProperties["eventType"] = eventType;
            msg.ApplicationProperties["level"] = level;
        }
        return msg;
    }

    private async Task<long> TotalActiveAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var rows = await http.GetFromJsonAsync<List<QueueRow>>($"{_opts.ManagementUrl.TrimEnd('/')}/queues/", ct) ?? [];
            var demo = new HashSet<string>(ReceivableEntities(), StringComparer.OrdinalIgnoreCase);
            return rows.Where(r => demo.Contains(r.Name)).Sum(r => r.ActiveMessageCount ?? 0);
        }
        catch { return 0; }
    }

    private long CurrentBoundary() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _opts.ResetIntervalSeconds;

    private async Task WaitForBrokerAsync(HttpClient http, CancellationToken ct)
    {
        for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                var resp = await http.GetAsync($"{_opts.ManagementUrl.TrimEnd('/')}/health", ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(1000, ct);
        }
    }

    private sealed record QueueRow(string Name, long? ActiveMessageCount);
}

public sealed record SeederOptions(string ConnectionString, string ManagementUrl, int ResetIntervalSeconds, int MaxActive);
