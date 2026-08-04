using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Explorer.Sessions;

namespace OpenServiceBus.Explorer.Api;

/// <summary>
/// Entity CRUD for the Explorer, driven by the real <see cref="ServiceBusAdministrationClient"/>.
/// The Explorer manages queues, topics, subscriptions, and rules over the broker's ATOM
/// management API - the exact protocol every SDK admin client speaks - so what the UI does is
/// what real client code does, and it works against any Service Bus-compatible endpoint the
/// connection string points at. Response shapes stay compatible with the UI's existing types.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Azure serializes "no TTL" as an enormous duration; the UI expects null.</summary>
    private static readonly TimeSpan UnlimitedThreshold = TimeSpan.FromDays(365 * 99);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        // --- Queues ---
        api.MapGet("/queues", (string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var admin = Admin(sessions, connectionString);
                var runtime = new Dictionary<string, QueueRuntimeProperties>(StringComparer.OrdinalIgnoreCase);
                await foreach (var r in admin.GetQueuesRuntimePropertiesAsync(ct))
                {
                    runtime[r.Name] = r;
                }
                var list = new List<object>();
                await foreach (var queue in admin.GetQueuesAsync(ct))
                {
                    list.Add(QueueDto(queue, runtime.GetValueOrDefault(queue.Name)));
                }
                return Results.Ok(list);
            }));

        api.MapPut("/queues/{name}", (string name, AdminEntityRequest body, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var admin = Admin(sessions, body.ConnectionString);
                var o = body.Options ?? new EntityOptions();
                var options = new CreateQueueOptions(name);
                if (o.MaxDeliveryCount is { } maxDelivery) options.MaxDeliveryCount = maxDelivery;
                if (o.LockDuration is { } lockDuration) options.LockDuration = lockDuration;
                if (o.DeadLetteringOnMessageExpiration is { } dle) options.DeadLetteringOnMessageExpiration = dle;
                if (o.DefaultMessageTimeToLive is { } ttl) options.DefaultMessageTimeToLive = ttl;
                if (o.RequiresSession is { } sessions_) options.RequiresSession = sessions_;
                if (o.RequiresDuplicateDetection is { } dedup) options.RequiresDuplicateDetection = dedup;
                if (o.DuplicateDetectionHistoryTimeWindow is { } window) options.DuplicateDetectionHistoryTimeWindow = window;
                if (!string.IsNullOrWhiteSpace(o.ForwardTo)) options.ForwardTo = o.ForwardTo;
                if (!string.IsNullOrWhiteSpace(o.ForwardDeadLetteredMessagesTo)) options.ForwardDeadLetteredMessagesTo = o.ForwardDeadLetteredMessagesTo;
                if (o.AutoDeleteOnIdle is { } idle) options.AutoDeleteOnIdle = idle;

                var created = (await admin.CreateQueueAsync(options, ct)).Value;
                return Results.Ok(QueueDto(created, null));
            }));

        api.MapDelete("/queues/{name}", (string name, string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                await Admin(sessions, connectionString).DeleteQueueAsync(name, ct);
                return Results.Ok(new { deleted = true });
            }));

        // --- Topics ---
        api.MapGet("/topics", (string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var list = new List<object>();
                await foreach (var topic in Admin(sessions, connectionString).GetTopicsAsync(ct))
                {
                    list.Add(new
                    {
                        name = topic.Name,
                        defaultMessageTimeToLive = Finite(topic.DefaultMessageTimeToLive),
                        status = topic.Status.ToString(),
                        autoDeleteOnIdle = Finite(topic.AutoDeleteOnIdle),
                    });
                }
                return Results.Ok(list);
            }));

        api.MapPut("/topics/{name}", (string name, AdminEntityRequest body, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var options = new CreateTopicOptions(name);
                if (body.Options?.DefaultMessageTimeToLive is { } ttl) options.DefaultMessageTimeToLive = ttl;
                if (body.Options?.AutoDeleteOnIdle is { } idle) options.AutoDeleteOnIdle = idle;
                var created = (await Admin(sessions, body.ConnectionString).CreateTopicAsync(options, ct)).Value;
                return Results.Ok(new
                {
                    name = created.Name,
                    defaultMessageTimeToLive = Finite(created.DefaultMessageTimeToLive),
                    status = created.Status.ToString(),
                    autoDeleteOnIdle = Finite(created.AutoDeleteOnIdle),
                });
            }));

        api.MapDelete("/topics/{name}", (string name, string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                await Admin(sessions, connectionString).DeleteTopicAsync(name, ct);
                return Results.Ok(new { deleted = true });
            }));

        // --- Subscriptions ---
        api.MapGet("/topics/{topic}/subscriptions", (string topic, string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var admin = Admin(sessions, connectionString);
                var runtime = new Dictionary<string, SubscriptionRuntimeProperties>(StringComparer.OrdinalIgnoreCase);
                await foreach (var r in admin.GetSubscriptionsRuntimePropertiesAsync(topic, ct))
                {
                    runtime[r.SubscriptionName] = r;
                }
                var list = new List<object>();
                await foreach (var sub in admin.GetSubscriptionsAsync(topic, ct))
                {
                    list.Add(SubscriptionDto(topic, sub, runtime.GetValueOrDefault(sub.SubscriptionName)));
                }
                return Results.Ok(list);
            }));

        api.MapPut("/topics/{topic}/subscriptions/{name}", (string topic, string name, AdminEntityRequest body, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var o = body.Options ?? new EntityOptions();
                var options = new CreateSubscriptionOptions(topic, name);
                if (o.MaxDeliveryCount is { } maxDelivery) options.MaxDeliveryCount = maxDelivery;
                if (o.LockDuration is { } lockDuration) options.LockDuration = lockDuration;
                if (o.DeadLetteringOnMessageExpiration is { } dle) options.DeadLetteringOnMessageExpiration = dle;
                if (o.DefaultMessageTimeToLive is { } ttl) options.DefaultMessageTimeToLive = ttl;
                if (o.RequiresSession is { } sessions_) options.RequiresSession = sessions_;
                if (!string.IsNullOrWhiteSpace(o.ForwardTo)) options.ForwardTo = o.ForwardTo;
                if (!string.IsNullOrWhiteSpace(o.ForwardDeadLetteredMessagesTo)) options.ForwardDeadLetteredMessagesTo = o.ForwardDeadLetteredMessagesTo;
                if (o.AutoDeleteOnIdle is { } subIdle) options.AutoDeleteOnIdle = subIdle;

                var created = (await Admin(sessions, body.ConnectionString).CreateSubscriptionAsync(options, ct)).Value;
                return Results.Ok(SubscriptionDto(topic, created, null));
            }));

        api.MapDelete("/topics/{topic}/subscriptions/{name}", (string topic, string name, string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                await Admin(sessions, connectionString).DeleteSubscriptionAsync(topic, name, ct);
                return Results.Ok(new { deleted = true });
            }));

        // --- Rules ---
        api.MapGet("/topics/{topic}/subscriptions/{sub}/rules", (string topic, string sub, string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var list = new List<object>();
                await foreach (var rule in Admin(sessions, connectionString).GetRulesAsync(topic, sub, ct))
                {
                    list.Add(RuleDto(rule));
                }
                return Results.Ok(list);
            }));

        api.MapPut("/topics/{topic}/subscriptions/{sub}/rules/{name}", (string topic, string sub, string name, AdminRuleRequest body, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                var admin = Admin(sessions, body.ConnectionString);
                var filter = BuildFilter(body.Rule);
                var action = string.IsNullOrWhiteSpace(body.Rule?.SqlAction) ? null : new SqlRuleAction(body.Rule.SqlAction);

                // The UI's save button is create-or-replace; the admin API distinguishes the
                // two, so fall through to update when the rule already exists.
                try
                {
                    var createOptions = new CreateRuleOptions(name, filter) { Action = action };
                    var created = (await admin.CreateRuleAsync(topic, sub, createOptions, ct)).Value;
                    return Results.Ok(RuleDto(created));
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
                {
                    RuleProperties existing = await admin.GetRuleAsync(topic, sub, name, ct);
                    existing.Filter = filter;
                    existing.Action = action;
                    var updated = (await admin.UpdateRuleAsync(topic, sub, existing, ct)).Value;
                    return Results.Ok(RuleDto(updated));
                }
            }));

        api.MapDelete("/topics/{topic}/subscriptions/{sub}/rules/{name}", (string topic, string sub, string name, string? connectionString, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                await Admin(sessions, connectionString).DeleteRuleAsync(topic, sub, name, ct);
                return Results.Ok(new { deleted = true });
            }));

        // --- Entity status (freeze/drain/reactivate through the admin client) ---
        api.MapPut("/status", (SetStatusRequest body, SessionManager sessions, CancellationToken ct) =>
            Guarded(async () =>
            {
                if (!Enum.TryParse<EntityStatus>(body.Status, ignoreCase: true, out var status))
                {
                    return Results.BadRequest(new { error = $"Unknown status '{body.Status}'." });
                }
                var admin = Admin(sessions, body.ConnectionString);
                switch (body.Kind?.ToLowerInvariant())
                {
                    case "queue":
                    {
                        QueueProperties queue = await admin.GetQueueAsync(body.Name, ct);
                        queue.Status = status;
                        await admin.UpdateQueueAsync(queue, ct);
                        break;
                    }
                    case "topic":
                    {
                        TopicProperties topic = await admin.GetTopicAsync(body.Name, ct);
                        topic.Status = status;
                        await admin.UpdateTopicAsync(topic, ct);
                        break;
                    }
                    case "subscription":
                    {
                        if (string.IsNullOrEmpty(body.Subscription))
                        {
                            return Results.BadRequest(new { error = "Subscription name is required for kind=subscription." });
                        }
                        SubscriptionProperties sub = await admin.GetSubscriptionAsync(body.Name, body.Subscription, ct);
                        sub.Status = status;
                        await admin.UpdateSubscriptionAsync(sub, ct);
                        break;
                    }
                    default:
                        return Results.BadRequest(new { error = $"Unknown entity kind '{body.Kind}'." });
                }
                return Results.Ok(new { status = status.ToString() });
            }));

        return endpoints;
    }

    private static ServiceBusAdministrationClient Admin(SessionManager sessions, string? clientConnectionString) =>
        sessions.GetOrCreate(ExplorerEndpoints.ResolveConnectionString(clientConnectionString)).Admin;

    /// <summary>Map admin-client failures onto the status codes and {error} shape the UI toasts.</summary>
    private static async Task<IResult> Guarded(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (ServiceBusException ex)
        {
            var status = ex.Reason switch
            {
                ServiceBusFailureReason.MessagingEntityNotFound => StatusCodes.Status404NotFound,
                ServiceBusFailureReason.MessagingEntityAlreadyExists => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status502BadGateway,
            };
            return Results.Json(new { error = ex.Message }, statusCode: status);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static object QueueDto(QueueProperties queue, QueueRuntimeProperties? runtime) => new
    {
        name = queue.Name,
        activeMessageCount = runtime?.ActiveMessageCount,
        deadLetterMessageCount = runtime?.DeadLetterMessageCount,
        status = queue.Status.ToString(),
        autoDeleteOnIdle = Finite(queue.AutoDeleteOnIdle),
        maxDeliveryCount = queue.MaxDeliveryCount,
        lockDuration = queue.LockDuration,
        deadLetteringOnMessageExpiration = queue.DeadLetteringOnMessageExpiration,
        defaultMessageTimeToLive = Finite(queue.DefaultMessageTimeToLive),
        requiresSession = queue.RequiresSession,
        requiresDuplicateDetection = queue.RequiresDuplicateDetection,
        duplicateDetectionHistoryTimeWindow = queue.RequiresDuplicateDetection ? queue.DuplicateDetectionHistoryTimeWindow : (TimeSpan?)null,
        forwardTo = queue.ForwardTo,
        forwardDeadLetteredMessagesTo = queue.ForwardDeadLetteredMessagesTo,
    };

    private static object SubscriptionDto(string topic, SubscriptionProperties sub, SubscriptionRuntimeProperties? runtime) => new
    {
        name = sub.SubscriptionName,
        topicName = topic,
        backingQueueName = $"{topic}/Subscriptions/{sub.SubscriptionName}",
        activeMessageCount = runtime?.ActiveMessageCount,
        deadLetterMessageCount = runtime?.DeadLetterMessageCount,
        status = sub.Status.ToString(),
        autoDeleteOnIdle = Finite(sub.AutoDeleteOnIdle),
        maxDeliveryCount = sub.MaxDeliveryCount,
        lockDuration = sub.LockDuration,
        deadLetteringOnMessageExpiration = sub.DeadLetteringOnMessageExpiration,
        defaultMessageTimeToLive = Finite(sub.DefaultMessageTimeToLive),
        requiresSession = sub.RequiresSession,
        forwardTo = sub.ForwardTo,
        forwardDeadLetteredMessagesTo = sub.ForwardDeadLetteredMessagesTo,
    };

    // The UI consumes rules FLAT (filterType + the correlation fields at top level).
    private static object RuleDto(RuleProperties rule) => rule.Filter switch
    {
        TrueRuleFilter => new { name = rule.Name, filterType = "true", sqlActionExpression = (rule.Action as SqlRuleAction)?.SqlExpression },
        FalseRuleFilter => new { name = rule.Name, filterType = "false", sqlActionExpression = (rule.Action as SqlRuleAction)?.SqlExpression },
        SqlRuleFilter sql => new
        {
            name = rule.Name,
            filterType = "sql",
            sqlExpression = sql.SqlExpression,
            sqlActionExpression = (rule.Action as SqlRuleAction)?.SqlExpression,
        } as object,
        CorrelationRuleFilter c => new
        {
            name = rule.Name,
            filterType = "correlation",
            messageId = c.MessageId,
            correlationId = c.CorrelationId,
            subject = c.Subject,
            to = c.To,
            replyTo = c.ReplyTo,
            replyToSessionId = c.ReplyToSessionId,
            sessionId = c.SessionId,
            contentType = c.ContentType,
            sqlActionExpression = (rule.Action as SqlRuleAction)?.SqlExpression,
            properties = c.ApplicationProperties.Count == 0
                ? null
                : c.ApplicationProperties.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString(), StringComparer.Ordinal),
        },
        _ => new { name = rule.Name, filterType = "unknown" },
    };

    private static RuleFilter BuildFilter(RulePayload? rule)
    {
        switch (rule?.FilterType?.ToLowerInvariant())
        {
            case "true":
                return new TrueRuleFilter();
            case "false":
                return new FalseRuleFilter();
            case "sql":
                if (string.IsNullOrWhiteSpace(rule.SqlExpression))
                {
                    throw new ArgumentException("sqlExpression is required for a SQL filter.");
                }
                return new SqlRuleFilter(rule.SqlExpression);
            case "correlation":
            {
                var filter = new CorrelationRuleFilter
                {
                    MessageId = NonEmpty(rule.MessageId),
                    CorrelationId = NonEmpty(rule.CorrelationId),
                    Subject = NonEmpty(rule.Subject),
                    To = NonEmpty(rule.To),
                    ReplyTo = NonEmpty(rule.ReplyTo),
                    ReplyToSessionId = NonEmpty(rule.ReplyToSessionId),
                    SessionId = NonEmpty(rule.SessionId),
                    ContentType = NonEmpty(rule.ContentType),
                };
                if (rule.Properties is { Count: > 0 })
                {
                    foreach (var (key, value) in rule.Properties)
                    {
                        filter.ApplicationProperties[key] = value;
                    }
                }
                return filter;
            }
            default:
                throw new ArgumentException($"Unknown filterType '{rule?.FilterType}'. Expected one of: true, false, sql, correlation.");
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static TimeSpan? Finite(TimeSpan value) => value >= UnlimitedThreshold ? null : value;
}

/// <summary>PUT body for queue/topic/subscription creation. Options keys mirror the UI's dialogs.</summary>
public sealed record AdminEntityRequest(string? ConnectionString, EntityOptions? Options);

public sealed record EntityOptions
{
    public int? MaxDeliveryCount { get; init; }
    public TimeSpan? LockDuration { get; init; }
    public bool? DeadLetteringOnMessageExpiration { get; init; }
    public TimeSpan? DefaultMessageTimeToLive { get; init; }
    public bool? RequiresSession { get; init; }
    public bool? RequiresDuplicateDetection { get; init; }
    public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; init; }
    public string? ForwardTo { get; init; }
    public string? ForwardDeadLetteredMessagesTo { get; init; }
    public TimeSpan? AutoDeleteOnIdle { get; init; }
}

/// <summary>PUT body for rules; the rule payload is flat, exactly as the UI's dialog builds it.</summary>
public sealed record AdminRuleRequest(string? ConnectionString, RulePayload? Rule);

/// <summary>PUT body for /api/status: kind = queue | topic | subscription.</summary>
public sealed record SetStatusRequest(string? ConnectionString, string? Kind, string Name, string? Subscription, string? Status);

public sealed record RulePayload
{
    public string? FilterType { get; init; }
    public string? SqlExpression { get; init; }
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? To { get; init; }
    public string? ReplyTo { get; init; }
    public string? ReplyToSessionId { get; init; }
    public string? SessionId { get; init; }
    public string? ContentType { get; init; }
    public Dictionary<string, string>? Properties { get; init; }
    public string? SqlAction { get; init; }
}
