using System.Net.Http.Json;
using System.Text;
using Azure.Messaging.ServiceBus;
using OpenServiceBus.Explorer.Metrics;
using OpenServiceBus.Explorer.Sessions;

namespace OpenServiceBus.Explorer.Api;

public static class ExplorerEndpoints
{
    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // --- Queue CRUD (proxied to broker's management REST API) ---
        api.MapGet("/queues", async (string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.GetAsync(Combine(managementUrl, "/queues/"), ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapPut("/queues/{name}", async (string name, CreateQueueRequest body, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var payload = JsonContent.Create(body.Options);
            var resp = await http.PutAsync(Combine(body.ManagementUrl, $"/queues/{name}"), payload, ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapDelete("/queues/{name}", async (string name, string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.DeleteAsync(Combine(managementUrl, $"/queues/{name}"), ct);
            return Results.StatusCode((int)resp.StatusCode);
        });

        // --- Data plane (real Azure SDK against the broker) ---
        api.MapPost("/send", async (SendRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            await using var sender = session.Sender(req.Queue);

            var count = Math.Max(1, req.Count ?? 1);
            var strategy = string.Equals(req.Strategy, "PARALLEL", StringComparison.OrdinalIgnoreCase)
                ? "PARALLEL"
                : "ATONCE";

            // Each copy gets a unique MessageId. If the user supplied a base id we suffix it (-0, -1, ...);
            // otherwise we generate a fresh Guid per copy. Without distinct ids dedup-enabled entities
            // would collapse the bulk into a single delivery.
            var baseId = string.IsNullOrWhiteSpace(req.MessageId) ? null : req.MessageId;
            ServiceBusMessage BuildOne(int index)
            {
                var msg = new ServiceBusMessage(req.Body ?? string.Empty);
                msg.MessageId = baseId is null
                    ? Guid.NewGuid().ToString("N")
                    : (count == 1 ? baseId : $"{baseId}-{index}");
                if (!string.IsNullOrWhiteSpace(req.CorrelationId)) msg.CorrelationId = req.CorrelationId;
                if (!string.IsNullOrWhiteSpace(req.Subject)) msg.Subject = req.Subject;
                if (!string.IsNullOrWhiteSpace(req.ContentType)) msg.ContentType = req.ContentType;
                if (!string.IsNullOrWhiteSpace(req.ReplyTo)) msg.ReplyTo = req.ReplyTo;
                if (!string.IsNullOrWhiteSpace(req.To)) msg.To = req.To;
                if (!string.IsNullOrWhiteSpace(req.SessionId)) msg.SessionId = req.SessionId;
                if (!string.IsNullOrWhiteSpace(req.PartitionKey)) msg.PartitionKey = req.PartitionKey;
                if (req.TimeToLiveSeconds is > 0) msg.TimeToLive = TimeSpan.FromSeconds(req.TimeToLiveSeconds.Value);
                if (req.ScheduledEnqueueTime is { } scheduled) msg.ScheduledEnqueueTime = scheduled;
                if (req.Properties is { Count: > 0 })
                {
                    foreach (var (k, v) in req.Properties) msg.ApplicationProperties[k] = v;
                }
                return msg;
            }

            string firstId;
            if (count == 1)
            {
                var msg = BuildOne(0);
                firstId = msg.MessageId;
                await sender.SendMessageAsync(msg, ct);
            }
            else if (strategy == "PARALLEL")
            {
                // PARALLEL: fire N independent SendMessageAsync calls concurrently. Each is its own
                // wire transfer/disposition pair, exercising the broker's per-message path.
                var msgs = Enumerable.Range(0, count).Select(BuildOne).ToList();
                firstId = msgs[0].MessageId;
                await Task.WhenAll(msgs.Select(m => sender.SendMessageAsync(m, ct)));
            }
            else
            {
                // ATONCE: a single SendMessagesAsync(IEnumerable<>) - the SDK packs them into one
                // AMQP transfer with one settlement, the real bulk path.
                var msgs = Enumerable.Range(0, count).Select(BuildOne).ToList();
                firstId = msgs[0].MessageId;
                await sender.SendMessagesAsync(msgs, ct);
            }

            return Results.Ok(new
            {
                sent = true,
                sentCount = count,
                strategy,
                messageId = firstId,
                scheduledFor = req.ScheduledEnqueueTime,
            });
        });

        api.MapPost("/receive", async (ReceiveRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            // When SessionId is provided we open (or reuse) a session-locked receiver via
            // AcceptSessionAsync - required for session-enabled queues and subscriptions.
            ServiceBusReceiver receiver = string.IsNullOrEmpty(req.SessionId)
                ? session.Receiver(req.Queue)
                : await session.SessionReceiverAsync(req.Queue, req.SessionId);
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(req.TimeoutSeconds ?? 5), ct);
            if (msg is null)
            {
                return Results.Ok(new { received = false });
            }
            session.TrackLocked(msg, receiver);
            return Results.Ok(ToDto(msg));
        });

        // Browse mode: returns a snapshot from the head of the queue without taking any locks.
        // Each call anchors at sequenceNumber 0 so the result is stable across repeated peeks.
        api.MapPost("/peek", async (PeekRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            var receiver = session.Receiver(req.Queue);
            var max = Math.Clamp(req.MaxMessages ?? 1, 1, 100);
            var msgs = await receiver.PeekMessagesAsync(max, fromSequenceNumber: 0, ct);
            var items = msgs.Select(m => ToDto(m, peekOnly: true)).ToList();
            return Results.Ok(new { count = items.Count, messages = items });
        });

        api.MapPost("/complete", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            if (!session.TryTakeLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token (already completed or never tracked by this explorer session)." });
            }
            await receiver.CompleteMessageAsync(msg, ct);
            return Results.Ok(new { completed = true });
        });

        api.MapPost("/abandon", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            if (!session.TryTakeLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.AbandonMessageAsync(msg, cancellationToken: ct);
            return Results.Ok(new { abandoned = true });
        });

        api.MapPost("/deadletter", async (DeadLetterRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            if (!session.TryTakeLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.DeadLetterMessageAsync(msg, req.Reason, req.Description, ct);
            return Results.Ok(new { deadLettered = true });
        });

        api.MapPost("/renew", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            // Renew uses the AMQP $management endpoint - does NOT consume the locked message,
            // so we look up but do NOT remove from the tracking dict.
            if (!session.TryPeekLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.RenewMessageLockAsync(msg, ct);
            return Results.Ok(new { renewedUntil = msg.LockedUntil });
        });

        api.MapPost("/defer", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            if (!session.TryTakeLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.DeferMessageAsync(msg, cancellationToken: ct);
            return Results.Ok(new { deferred = true, sequenceNumber = msg.SequenceNumber });
        });

        // Requeue: re-publish a copy of a locked DLQ message onto its source entity and complete
        // the DLQ original. For a queue DLQ that source is the parent queue; for a subscription
        // DLQ it is the topic (so the broker re-evaluates rules and fans out again).
        api.MapPost("/requeue", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(req.ConnectionString);
            if (!session.TryTakeLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token (already settled or never tracked)." });
            }
            var target = ComputeRequeueTarget(req.Queue);
            if (target is null)
            {
                return Results.BadRequest(new { error = $"Cannot derive requeue target from '{req.Queue}'." });
            }

            var copy = new ServiceBusMessage(msg.Body)
            {
                MessageId = msg.MessageId,
            };
            if (!string.IsNullOrEmpty(msg.CorrelationId)) copy.CorrelationId = msg.CorrelationId;
            if (!string.IsNullOrEmpty(msg.Subject)) copy.Subject = msg.Subject;
            if (!string.IsNullOrEmpty(msg.ContentType)) copy.ContentType = msg.ContentType;
            if (!string.IsNullOrEmpty(msg.ReplyTo)) copy.ReplyTo = msg.ReplyTo;
            if (!string.IsNullOrEmpty(msg.To)) copy.To = msg.To;
            if (!string.IsNullOrEmpty(msg.SessionId)) copy.SessionId = msg.SessionId;
            if (!string.IsNullOrEmpty(msg.PartitionKey)) copy.PartitionKey = msg.PartitionKey;
            // TimeToLive is TimeSpan.MaxValue when the broker treats it as "infinite" -
            // forwarding that to a sender raises. Skip it; the destination queue's default applies.
            if (msg.TimeToLive != default && msg.TimeToLive < TimeSpan.FromDays(1000)) copy.TimeToLive = msg.TimeToLive;
            foreach (var kv in msg.ApplicationProperties)
            {
                // Strip broker-stamped DLQ markers - the new copy starts clean.
                if (kv.Key is "DeadLetterReason" or "DeadLetterErrorDescription" or "Diagnostic-Id") continue;
                copy.ApplicationProperties[kv.Key] = kv.Value;
            }

            await using var sender = session.Sender(target);
            await sender.SendMessageAsync(copy, ct);
            await receiver.CompleteMessageAsync(msg, ct);
            return Results.Ok(new { requeued = true, target, messageId = copy.MessageId });
        });

        // --- Topic CRUD (proxied) ---
        api.MapGet("/topics", async (string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.GetAsync(Combine(managementUrl, "/topics/"), ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapPut("/topics/{name}", async (string name, CreateTopicProxyRequest body, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var payload = JsonContent.Create(body.Options ?? new());
            var resp = await http.PutAsync(Combine(body.ManagementUrl, $"/topics/{name}"), payload, ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapDelete("/topics/{name}", async (string name, string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.DeleteAsync(Combine(managementUrl, $"/topics/{name}"), ct);
            return Results.StatusCode((int)resp.StatusCode);
        });

        // --- Subscription CRUD (proxied) ---
        api.MapGet("/topics/{topic}/subscriptions", async (string topic, string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.GetAsync(Combine(managementUrl, $"/topics/{topic}/subscriptions"), ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapPut("/topics/{topic}/subscriptions/{name}", async (string topic, string name, CreateSubscriptionProxyRequest body, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var payload = JsonContent.Create(body.Options ?? new());
            var resp = await http.PutAsync(Combine(body.ManagementUrl, $"/topics/{topic}/subscriptions/{name}"), payload, ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapDelete("/topics/{topic}/subscriptions/{name}", async (string topic, string name, string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.DeleteAsync(Combine(managementUrl, $"/topics/{topic}/subscriptions/{name}"), ct);
            return Results.StatusCode((int)resp.StatusCode);
        });

        // --- Rule CRUD (proxied) ---
        api.MapGet("/topics/{topic}/subscriptions/{sub}/rules", async (string topic, string sub, string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.GetAsync(Combine(managementUrl, $"/topics/{topic}/subscriptions/{sub}/rules"), ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapPut("/topics/{topic}/subscriptions/{sub}/rules/{name}", async (string topic, string sub, string name, CreateRuleProxyRequest body, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var payload = JsonContent.Create(body.Rule);
            var resp = await http.PutAsync(Combine(body.ManagementUrl, $"/topics/{topic}/subscriptions/{sub}/rules/{name}"), payload, ct);
            return Results.Content(await resp.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)resp.StatusCode);
        });

        api.MapDelete("/topics/{topic}/subscriptions/{sub}/rules/{name}", async (string topic, string sub, string name, string managementUrl, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var http = httpFactory.CreateClient();
            var resp = await http.DeleteAsync(Combine(managementUrl, $"/topics/{topic}/subscriptions/{sub}/rules/{name}"), ct);
            return Results.StatusCode((int)resp.StatusCode);
        });

        // --- Connectivity check ---
        api.MapPost("/ping", async (PingRequest req, SessionManager sessions, MetricsCollector metrics, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            // Start historical sampling of this broker (idempotent).
            metrics.RegisterManagementUrl(req.ManagementUrl);

            var http = httpFactory.CreateClient();
            string? mgmtStatus = null;
            string? sdkStatus = null;

            try
            {
                var resp = await http.GetAsync(Combine(req.ManagementUrl, "/health"), ct);
                mgmtStatus = $"{(int)resp.StatusCode} {resp.StatusCode}";
            }
            catch (Exception ex)
            {
                mgmtStatus = "error: " + ex.Message;
            }

            try
            {
                var session = sessions.GetOrCreate(req.ConnectionString);
                // Cheap probe: open a sender then close it. ServiceBusClient opens on first send/receive.
                await using var sender = session.Sender("__ping__probe__");
                sdkStatus = "ok (client constructed; actual AMQP open happens on first send/receive)";
            }
            catch (Exception ex)
            {
                sdkStatus = "error: " + ex.Message;
            }

            return Results.Ok(new { management = mgmtStatus, serviceBus = sdkStatus });
        });

        // Historical metrics for an entity: its own sampled series plus its dead-letter
        // queue's series, from MetricsCollector. Each sample carries the current depth
        // (active) and cumulative lifetime counters (enqueued, completed); the client
        // derives per-interval throughput by differencing consecutive samples, and reads
        // the dead-letter level/rate from the DLQ series. windowSeconds selects the
        // lookback (1800 = 30 min … 86400 = 24 h).
        api.MapGet("/metrics", (string entity, int windowSeconds, MetricsCollector metrics) =>
        {
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Max(60, windowSeconds);
            return Results.Ok(new
            {
                entity = metrics.Series(entity, since),
                dlq = metrics.Series(entity + "/$DeadLetterQueue", since),
            });
        });

        return endpoints;
    }

    private static string Combine(string baseUrl, string suffix)
        => baseUrl.TrimEnd('/') + suffix;

    // For "<queue>/$DeadLetterQueue" the target is the parent queue.
    // For "<topic>/Subscriptions/<sub>/$DeadLetterQueue" the target is the topic (so the broker
    // re-evaluates subscription filters and fans the message out again).
    private static string? ComputeRequeueTarget(string queue)
    {
        const string dlqSuffix = "/$DeadLetterQueue";
        if (!queue.EndsWith(dlqSuffix, StringComparison.OrdinalIgnoreCase)) return null;
        var stripped = queue[..^dlqSuffix.Length];
        const string subSegment = "/Subscriptions/";
        var idx = stripped.IndexOf(subSegment, StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? stripped[..idx] : stripped;
    }

    // When peekOnly is true, lockToken and lockedUntil are nulled out - the client uses null
    // lockToken as the signal that no dispositions are available for this message.
    private static object ToDto(ServiceBusReceivedMessage msg, bool peekOnly = false) => new
    {
        received = true,
        sequenceNumber = Safe(() => (long?)msg.SequenceNumber),
        messageId = msg.MessageId,
        correlationId = msg.CorrelationId,
        subject = msg.Subject,
        contentType = msg.ContentType,
        enqueuedTime = SafeTime(() => msg.EnqueuedTime),
        lockedUntil = peekOnly ? null : SafeTime(() => msg.LockedUntil),
        expiresAt = SafeTime(() => msg.ExpiresAt),  // TTL deadline
        timeToLive = Safe(() => (TimeSpan?)msg.TimeToLive),
        deliveryCount = Safe(() => (int?)msg.DeliveryCount),
        lockToken = peekOnly ? null : msg.LockToken,
        body = SafeBody(msg.Body),
        applicationProperties = msg.ApplicationProperties.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()),
        // Dead-letter metadata - populated only on messages received from a DLQ.
        deadLetterReason = msg.DeadLetterReason,
        deadLetterErrorDescription = msg.DeadLetterErrorDescription,
        deadLetterSource = msg.DeadLetterSource,
    };

    /// <summary>
    /// Several <see cref="ServiceBusReceivedMessage"/> properties dereference Nullable and NRE
    /// if the broker hasn't stamped the corresponding AMQP header/annotation. Wrap to return null
    /// rather than fail the whole response.
    /// </summary>
    private static T? Safe<T>(Func<T?> get) where T : struct
    {
        try { return get(); } catch { return null; }
    }

    /// <summary>
    /// Like <see cref="Safe"/> but also filters out <c>default(DateTimeOffset)</c> (0001-01-01),
    /// which the SDK returns when an annotation is absent. Returning null keeps the UI clean.
    /// </summary>
    private static DateTimeOffset? SafeTime(Func<DateTimeOffset> get)
    {
        try
        {
            var value = get();
            return value == default ? null : value;
        }
        catch { return null; }
    }

    private static string SafeBody(BinaryData body)
    {
        try { return body.ToString(); }
        catch { return $"<{body.ToMemory().Length} bytes>"; }
    }
}

public sealed record CreateQueueRequest(string ManagementUrl, Dictionary<string, object>? Options);
public sealed record CreateTopicProxyRequest(string ManagementUrl, Dictionary<string, object>? Options);
public sealed record CreateSubscriptionProxyRequest(string ManagementUrl, Dictionary<string, object>? Options);
public sealed record CreateRuleProxyRequest(string ManagementUrl, Dictionary<string, object?> Rule);
public sealed record SendRequest(
    string ConnectionString,
    string Queue,
    string? Body,
    string? MessageId,
    string? CorrelationId,
    string? Subject,
    string? ContentType,
    string? ReplyTo,
    string? To,
    string? SessionId,
    string? PartitionKey,
    int? TimeToLiveSeconds,
    DateTimeOffset? ScheduledEnqueueTime,
    Dictionary<string, string>? Properties,
    int? Count,
    string? Strategy);
public sealed record ReceiveRequest(string ConnectionString, string Queue, int? TimeoutSeconds, string? SessionId);
public sealed record PeekRequest(string ConnectionString, string Queue, int? MaxMessages);
public sealed record DispositionRequest(string ConnectionString, string Queue, string LockToken);
public sealed record DeadLetterRequest(string ConnectionString, string Queue, string LockToken, string? Reason, string? Description);
public sealed record PingRequest(string ConnectionString, string ManagementUrl);
