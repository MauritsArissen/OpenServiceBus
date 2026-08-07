using Azure.Messaging.ServiceBus;
using OpenServiceBus.Explorer.Metrics;
using OpenServiceBus.Explorer.Sessions;

namespace OpenServiceBus.Explorer.Api;

public static class ExplorerEndpoints
{
    // Bulk-send and receive-timeout ceilings. The Explorer is a manual testing tool, not a load
    // generator; without a cap a single request (Count = 100_000_000, or a multi-week timeout)
    // can exhaust the co-located broker or pin a request slot - a trivial DoS on the hosted demo.
    private const int MaxSendCount = 1000;
    private const int MaxReceiveTimeoutSeconds = 60;
    private const int MaxBulkTokens = 1000;

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

    // Shared with AdminEndpoints - entity CRUD resolves its broker the same pinned-in-demo way.
    internal static string ResolveConnectionString(string? clientValue) =>
        DemoMode && PinnedConnectionString is not null ? PinnedConnectionString : (clientValue ?? string.Empty);

    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // Front-end bootstrap config. In the hosted live demo (OSB_EXPLORER_DEMO=true) this
        // pins the connection to the co-located broker and tells the UI to lock the
        // connection inputs and show a reset countdown. Empty/false everywhere else, so a
        // normal Explorer is completely unaffected.
        api.MapGet("/config", () =>
        {
            static string? Env(string name) =>
                Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

            var demo = string.Equals(Env("OSB_EXPLORER_DEMO"), "true", StringComparison.OrdinalIgnoreCase);
            var reset = int.TryParse(Env("OSB_EXPLORER_RESET_INTERVAL_SECONDS"), out var r) ? r : 1800;
            return Results.Ok(new
            {
                demoMode = demo,
                connectionString = Env("OSB_EXPLORER_CONNECTION"),
                managementUrl = Env("OSB_EXPLORER_MGMT_URL"),
                resetIntervalSeconds = reset,
                version = Env("OSB_EXPLORER_VERSION"),
            });
        });

        // Entity CRUD lives in AdminEndpoints - it drives the broker through the real
        // ServiceBusAdministrationClient (the ATOM management API on the AMQP port).

        // --- Data plane (real Azure SDK against the broker) ---
        api.MapPost("/send", async (SendRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            await using var sender = session.Sender(req.Queue);

            var count = Math.Clamp(req.Count ?? 1, 1, MaxSendCount);
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
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            // When SessionId is provided we open (or reuse) a session-locked receiver via
            // AcceptSessionAsync - required for session-enabled queues and subscriptions.
            ServiceBusReceiver receiver = string.IsNullOrEmpty(req.SessionId)
                ? session.Receiver(req.Queue)
                : await session.SessionReceiverAsync(req.Queue, req.SessionId);
            var timeoutSeconds = Math.Clamp(req.TimeoutSeconds ?? 5, 1, MaxReceiveTimeoutSeconds);
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
            if (msg is null)
            {
                return Results.Ok(new { received = false });
            }
            session.TrackLocked(msg, receiver);
            return Results.Ok(ToDto(msg));
        });

        // Browse mode: returns a snapshot without taking any locks. The client owns the peek
        // cursor: it passes fromSequenceNumber explicitly (0 = head of the queue) and continues
        // with lastPeekedSequenceNumber + 1, mirroring how the real Service Bus SDKs enumerate.
        // The receiver is pooled across HTTP requests, so relying on the SDK's implicit
        // per-receiver cursor would make results depend on unrelated earlier calls.
        api.MapPost("/peek", async (PeekRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            var receiver = session.Receiver(req.Queue);
            var max = Math.Clamp(req.MaxMessages ?? 1, 1, 100);
            var from = Math.Max(0, req.FromSequenceNumber ?? 0);
            var msgs = await receiver.PeekMessagesAsync(max, fromSequenceNumber: from, ct);
            var items = msgs.Select(m => ToDto(m, peekOnly: true)).ToList();
            return Results.Ok(new { count = items.Count, messages = items });
        });

        api.MapPost("/complete", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            // Look up WITHOUT removing, settle at the broker, and only then drop the tracking
            // entry. If the broker call fails the lock stays tracked so the user can retry -
            // removing first would strand the message until its lock expired.
            if (!session.TryPeekLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token (already completed or never tracked by this explorer session)." });
            }
            await receiver.CompleteMessageAsync(msg, ct);
            session.TryTakeLocked(req.LockToken, out _, out _);
            return Results.Ok(new { completed = true });
        });

        api.MapPost("/abandon", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            if (!session.TryPeekLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.AbandonMessageAsync(msg, cancellationToken: ct);
            session.TryTakeLocked(req.LockToken, out _, out _);
            return Results.Ok(new { abandoned = true });
        });

        api.MapPost("/deadletter", async (DeadLetterRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            if (!session.TryPeekLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.DeadLetterMessageAsync(msg, req.Reason, req.Description, ct);
            session.TryTakeLocked(req.LockToken, out _, out _);
            return Results.Ok(new { deadLettered = true });
        });

        api.MapPost("/renew", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
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
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            if (!session.TryPeekLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token." });
            }
            await receiver.DeferMessageAsync(msg, cancellationToken: ct);
            session.TryTakeLocked(req.LockToken, out _, out _);
            return Results.Ok(new { deferred = true, sequenceNumber = msg.SequenceNumber });
        });

        // Requeue: re-publish a copy of a locked DLQ message onto its source entity and complete
        // the DLQ original. For a queue DLQ that source is the parent queue; for a subscription
        // DLQ it is the topic (so the broker re-evaluates rules and fans out again).
        api.MapPost("/requeue", async (DispositionRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            // Peek (don't remove) so a mid-operation failure leaves the DLQ message tracked and
            // re-settleable rather than stranded. The tracking entry is dropped only after the
            // copy is published AND the original is completed.
            if (!session.TryPeekLocked(req.LockToken, out var msg, out var receiver) || msg is null || receiver is null)
            {
                return Results.NotFound(new { error = "Unknown lock token (already settled or never tracked)." });
            }
            var target = ComputeRequeueTarget(req.Queue);
            if (target is null)
            {
                return Results.BadRequest(new { error = $"Cannot derive requeue target from '{req.Queue}'." });
            }

            var copy = BuildRequeueCopy(msg);

            await using var sender = session.Sender(target);
            await sender.SendMessageAsync(copy, ct);
            await receiver.CompleteMessageAsync(msg, ct);
            session.TryTakeLocked(req.LockToken, out _, out _);
            return Results.Ok(new { requeued = true, target, messageId = copy.MessageId });
        });

        // Bulk dispositions: one request settles many tracked locks. Deliberately NOT atomic -
        // each lock token is settled independently and reported per token, so one expired lock
        // never aborts the rest of the batch.
        api.MapPost("/bulk", async (BulkRequest req, SessionManager sessions, CancellationToken ct) =>
        {
            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            var action = (req.Action ?? string.Empty).ToLowerInvariant();
            if (action is not ("complete" or "abandon" or "deadletter" or "defer" or "requeue"))
            {
                return Results.BadRequest(new { error = $"Unknown bulk action '{req.Action}'. Expected complete, abandon, deadletter, defer or requeue." });
            }
            if (req.LockTokens is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "lockTokens must contain at least one lock token." });
            }
            if (req.LockTokens.Count > MaxBulkTokens)
            {
                return Results.BadRequest(new { error = $"At most {MaxBulkTokens} lock tokens per bulk request." });
            }

            string? requeueTarget = null;
            if (action == "requeue")
            {
                requeueTarget = ComputeRequeueTarget(req.Queue);
                if (requeueTarget is null)
                {
                    return Results.BadRequest(new { error = $"Cannot derive requeue target from '{req.Queue}'." });
                }
            }

            var results = new List<BulkItemResult>(req.LockTokens.Count);
            foreach (var token in req.LockTokens)
            {
                if (!session.TryPeekLocked(token, out var msg, out var receiver) || msg is null || receiver is null)
                {
                    results.Add(new BulkItemResult(token, false, true,
                        "Unknown lock token (already settled or never tracked by this explorer session)."));
                    continue;
                }
                try
                {
                    switch (action)
                    {
                        case "complete":
                            await receiver.CompleteMessageAsync(msg, ct);
                            break;
                        case "abandon":
                            await receiver.AbandonMessageAsync(msg, cancellationToken: ct);
                            break;
                        case "deadletter":
                            await receiver.DeadLetterMessageAsync(msg, req.Reason, req.Description, ct);
                            break;
                        case "defer":
                            await receiver.DeferMessageAsync(msg, cancellationToken: ct);
                            break;
                        case "requeue":
                            var copy = BuildRequeueCopy(msg);
                            await using (var sender = session.Sender(requeueTarget!))
                            {
                                await sender.SendMessageAsync(copy, ct);
                            }
                            await receiver.CompleteMessageAsync(msg, ct);
                            break;
                    }
                    session.TryTakeLocked(token, out _, out _);
                    results.Add(new BulkItemResult(token, true, false, null));
                }
                catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.MessageLockLost or ServiceBusFailureReason.SessionLockLost)
                {
                    // A lost lock can never be retried - drop the stale tracking entry so the
                    // client removes the row instead of offering a settle that must fail.
                    session.TryTakeLocked(token, out _, out _);
                    results.Add(new BulkItemResult(token, false, true, "Lock lost - the message became available to other receivers again."));
                }
                catch (Exception ex)
                {
                    results.Add(new BulkItemResult(token, false, false, ex.Message));
                }
            }

            var succeeded = results.Count(r => r.Ok);
            return Results.Ok(new
            {
                action,
                total = results.Count,
                succeeded,
                failed = results.Count - succeeded,
                results,
            });
        });

        // --- Connectivity check ---
        api.MapPost("/ping", async (PingRequest req, SessionManager sessions, MetricsCollector metrics, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            // Start historical sampling of this broker (idempotent).
            var pingMgmt = ResolveManagementUrl(req.ManagementUrl);
            metrics.RegisterManagementUrl(pingMgmt);

            var http = httpFactory.CreateClient();
            string? mgmtStatus = null;
            string? sdkStatus = null;
            string? atomStatus = null;
            BrokerInfo? broker = null;

            try
            {
                var resp = await http.GetAsync(Combine(pingMgmt, "/health"), ct);
                mgmtStatus = $"{(int)resp.StatusCode} {resp.StatusCode}";
            }
            catch (Exception ex)
            {
                mgmtStatus = "error: " + ex.Message;
            }

            // Emulator detection: the OpenServiceBus management root identifies itself and
            // lists emulator-native capabilities (e.g. purge). A real Azure namespace has no
            // reachable management URL, so `broker` stays null and native features stay off.
            try
            {
                var info = await http.GetFromJsonAsync<BrokerInfo>(Combine(pingMgmt, "/"), ct);
                if (string.Equals(info?.Name, "OpenServiceBus", StringComparison.OrdinalIgnoreCase))
                {
                    broker = info;
                }
            }
            catch
            {
                broker = null;
            }

            var session = sessions.GetOrCreate(ResolveConnectionString(req.ConnectionString));
            try
            {
                // Cheap probe: open a sender then close it. ServiceBusClient opens on first send/receive.
                await using var sender = session.Sender("__ping__probe__");
                sdkStatus = "ok (client constructed; actual AMQP open happens on first send/receive)";
            }
            catch (Exception ex)
            {
                sdkStatus = "error: " + ex.Message;
            }

            try
            {
                // A real management-plane round trip: the SDK admin client against the ATOM
                // API on the broker's AMQP port - the same path entity CRUD uses.
                var ns = (await session.Admin.GetNamespacePropertiesAsync(ct)).Value;
                atomStatus = $"ok (namespace '{ns.Name}')";
            }
            catch (Exception ex)
            {
                atomStatus = "error: " + ex.Message;
            }

            return Results.Ok(new { management = mgmtStatus, serviceBus = sdkStatus, atomManagement = atomStatus, broker });
        });

        // --- Purge (OpenServiceBus-native; proxied to the JSON management API) ---
        api.MapPost("/purge", async (PurgeRequest req, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var mgmt = ResolveManagementUrl(req.ManagementUrl);
            if (string.IsNullOrWhiteSpace(mgmt))
            {
                return Results.BadRequest(new { error = "Purge is an OpenServiceBus-native operation and needs the broker's management URL." });
            }

            var suffix = req.DeadLetterOnly == true ? "?subqueue=deadletter" : string.Empty;
            var http = httpFactory.CreateClient();
            HttpResponseMessage resp;
            if (!string.IsNullOrEmpty(req.Topic) && !string.IsNullOrEmpty(req.Subscription))
            {
                resp = await http.DeleteAsync(
                    Combine(mgmt, $"/topics/{Uri.EscapeDataString(req.Topic)}/subscriptions/{Uri.EscapeDataString(req.Subscription)}/messages{suffix}"), ct);
            }
            else if (!string.IsNullOrEmpty(req.Topic))
            {
                resp = await http.DeleteAsync(Combine(mgmt, $"/topics/{Uri.EscapeDataString(req.Topic)}/messages"), ct);
            }
            else if (!string.IsNullOrEmpty(req.Queue))
            {
                resp = await http.DeleteAsync(Combine(mgmt, $"/queues/{Uri.EscapeDataString(req.Queue)}/messages{suffix}"), ct);
            }
            else
            {
                resp = await http.PostAsync(Combine(mgmt, "/purge"), content: null, ct);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(string.IsNullOrEmpty(body) ? "{}" : body, "application/json", statusCode: (int)resp.StatusCode);
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

    // A requeue re-publishes a clean copy of a DLQ message: same identity and payload, but
    // TimeToLive is skipped when the broker reported it as effectively infinite (forwarding
    // TimeSpan.MaxValue to a sender raises) and broker-stamped DLQ markers are stripped.
    private static ServiceBusMessage BuildRequeueCopy(ServiceBusReceivedMessage msg)
    {
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
        if (msg.TimeToLive != default && msg.TimeToLive < TimeSpan.FromDays(1000)) copy.TimeToLive = msg.TimeToLive;
        foreach (var kv in msg.ApplicationProperties)
        {
            if (kv.Key is "DeadLetterReason" or "DeadLetterErrorDescription" or "Diagnostic-Id") continue;
            copy.ApplicationProperties[kv.Key] = kv.Value;
        }
        return copy;
    }

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
public sealed record PeekRequest(string ConnectionString, string Queue, int? MaxMessages, long? FromSequenceNumber);
public sealed record DispositionRequest(string ConnectionString, string Queue, string LockToken);
public sealed record BulkRequest(string ConnectionString, string Queue, string? Action, List<string>? LockTokens, string? Reason, string? Description);
public sealed record BulkItemResult(string LockToken, bool Ok, bool LockLost, string? Error);
public sealed record DeadLetterRequest(string ConnectionString, string Queue, string LockToken, string? Reason, string? Description);
public sealed record PingRequest(string ConnectionString, string ManagementUrl);
public sealed record PurgeRequest(string? ManagementUrl, string? Queue, string? Topic, string? Subscription, bool? DeadLetterOnly);
public sealed record BrokerInfo(string? Name, string? Version, string[]? Capabilities);
