using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Management.Endpoints;

public static class TopicEndpoints
{
    /// <summary>
    /// REST surface for the topic/subscription/rule pub-sub model, under <c>/topics</c>.
    /// Mirrors the shape of <c>/queues</c>; subscriptions live under <c>/topics/{topic}/subscriptions</c>
    /// and their rules under <c>.../rules</c>. The Explorer UI proxies this surface so a browser
    /// can drive the full topology end-to-end without touching the AMQP wire directly.
    /// </summary>
    public static IEndpointRouteBuilder MapTopicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/topics");

        // --- Topics ---
        group.MapGet("/", async (ITopicRegistry topics, CancellationToken ct) =>
        {
            var list = await topics.ListTopicsAsync(ct);
            return Results.Ok(list.Select(TopicResponse.From));
        });

        group.MapGet("/{name}", async (string name, ITopicRegistry topics, CancellationToken ct) =>
        {
            var topic = await topics.GetTopicAsync(name, ct);
            return topic is null ? Results.NotFound() : Results.Ok(TopicResponse.From(topic));
        });

        group.MapPut("/{name}", async (string name, CreateTopicRequest? body, ITopicRegistry topics, CancellationToken ct) =>
        {
            var descriptor = new TopicDescriptor
            {
                Name = name,
                DefaultMessageTimeToLive = body?.DefaultMessageTimeToLive,
                Status = body?.Status ?? EntityStatus.Active,
            };
            // Idempotent re-declaration is fine; an attempt to change settings on an existing
            // topic is rejected rather than silently ignored (create is GetOrAdd - it returns the
            // existing descriptor unchanged).
            var existing = await topics.GetTopicAsync(name, ct);
            if (existing is not null)
            {
                if (existing.DefaultMessageTimeToLive != descriptor.DefaultMessageTimeToLive
                    || existing.Status != descriptor.Status)
                {
                    return Results.Conflict(new
                    {
                        error = $"Topic '{name}' already exists and its properties cannot be updated after creation.",
                    });
                }
                return Results.Ok(TopicResponse.From(existing));
            }
            var created = await topics.CreateTopicAsync(descriptor, ct);
            return Results.Ok(TopicResponse.From(created));
        });

        group.MapDelete("/{name}", async (string name, ITopicRegistry topics, CancellationToken ct) =>
        {
            var deleted = await topics.DeleteTopicAsync(name, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // --- Subscriptions ---
        group.MapGet("/{topic}/subscriptions", async (string topic, ITopicRegistry topics, IMessageStore store, CancellationToken ct) =>
        {
            var subs = await topics.ListSubscriptionsAsync(topic, ct);
            var response = new List<SubscriptionResponse>(subs.Count);
            foreach (var sub in subs)
            {
                var count = await store.CountAsync(sub.BackingQueueName, ct);
                response.Add(SubscriptionResponse.From(sub, count));
            }
            return Results.Ok(response);
        });

        group.MapGet("/{topic}/subscriptions/{name}", async (string topic, string name, ITopicRegistry topics, IMessageStore store, CancellationToken ct) =>
        {
            var sub = await topics.GetSubscriptionAsync(topic, name, ct);
            if (sub is null) return Results.NotFound();
            var count = await store.CountAsync(sub.BackingQueueName, ct);
            return Results.Ok(SubscriptionResponse.From(sub, count));
        });

        group.MapPut("/{topic}/subscriptions/{name}", async (string topic, string name, CreateSubscriptionRequest? body, ITopicRegistry topics, CancellationToken ct) =>
        {
            var b = body ?? new CreateSubscriptionRequest();
            var descriptor = new SubscriptionDescriptor
            {
                TopicName = topic,
                Name = name,
                MaxDeliveryCount = b.MaxDeliveryCount,
                LockDuration = b.LockDuration,
                DeadLetteringOnMessageExpiration = b.DeadLetteringOnMessageExpiration,
                DefaultMessageTimeToLive = b.DefaultMessageTimeToLive,
                RequiresSession = b.RequiresSession,
                ForwardTo = b.ForwardTo,
                ForwardDeadLetteredMessagesTo = b.ForwardDeadLetteredMessagesTo,
                Status = b.Status,
            };
            // Same create-vs-update honesty as queues: re-declaring identical settings is a 200,
            // trying to change them on an existing subscription is a 409 instead of a silent no-op.
            var existingSub = await topics.GetSubscriptionAsync(topic, name, ct);
            if (existingSub is not null)
            {
                if (!SubscriptionMatchesRequest(existingSub, descriptor))
                {
                    return Results.Conflict(new
                    {
                        error = $"Subscription '{topic}/{name}' already exists and its properties cannot be updated after creation.",
                    });
                }
                return Results.Ok(SubscriptionResponse.From(existingSub));
            }
            try
            {
                var created = await topics.CreateSubscriptionAsync(descriptor, ct);
                return Results.Ok(SubscriptionResponse.From(created));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{topic}/subscriptions/{name}", async (string topic, string name, ITopicRegistry topics, CancellationToken ct) =>
        {
            var deleted = await topics.DeleteSubscriptionAsync(topic, name, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // --- Rules ---
        group.MapGet("/{topic}/subscriptions/{sub}/rules", async (string topic, string sub, ITopicRegistry topics, CancellationToken ct) =>
        {
            var rules = await topics.ListRulesAsync(topic, sub, ct);
            return Results.Ok(rules.Select(RuleResponse.From));
        });

        group.MapPut("/{topic}/subscriptions/{sub}/rules/{name}", async (string topic, string sub, string name, CreateRuleRequest body, ITopicRegistry topics, CancellationToken ct) =>
        {
            RuleFilter filter;
            try
            {
                filter = body.ToFilter();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            try
            {
                var rule = await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = topic,
                    SubscriptionName = sub,
                    Name = name,
                    Filter = filter,
                }, ct);
                return Results.Ok(RuleResponse.From(rule));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{topic}/subscriptions/{sub}/rules/{name}", async (string topic, string sub, string name, ITopicRegistry topics, CancellationToken ct) =>
        {
            var deleted = await topics.DeleteRuleAsync(topic, sub, name, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    // Distinguishes an idempotent subscription re-declaration from an attempted settings update.
    private static bool SubscriptionMatchesRequest(SubscriptionDescriptor existing, SubscriptionDescriptor requested) =>
        existing.MaxDeliveryCount == requested.MaxDeliveryCount
        && existing.LockDuration == requested.LockDuration
        && existing.DeadLetteringOnMessageExpiration == requested.DeadLetteringOnMessageExpiration
        && existing.DefaultMessageTimeToLive == requested.DefaultMessageTimeToLive
        && existing.RequiresSession == requested.RequiresSession
        && string.Equals(existing.ForwardTo, requested.ForwardTo, StringComparison.Ordinal)
        && string.Equals(existing.ForwardDeadLetteredMessagesTo, requested.ForwardDeadLetteredMessagesTo, StringComparison.Ordinal)
        && existing.Status == requested.Status;
}

public sealed record CreateTopicRequest
{
    public TimeSpan? DefaultMessageTimeToLive { get; init; }
    public EntityStatus Status { get; init; } = EntityStatus.Active;
}

public sealed record CreateSubscriptionRequest
{
    public int MaxDeliveryCount { get; init; } = 10;
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(60);
    public bool DeadLetteringOnMessageExpiration { get; init; }
    public TimeSpan? DefaultMessageTimeToLive { get; init; }
    public bool RequiresSession { get; init; }
    public string? ForwardTo { get; init; }
    public string? ForwardDeadLetteredMessagesTo { get; init; }
    public EntityStatus Status { get; init; } = EntityStatus.Active;
}

/// <summary>
/// Wire shape for creating/replacing a rule. Tag tells the server which filter flavour to
/// instantiate; only the relevant payload field is read.
/// </summary>
public sealed record CreateRuleRequest
{
    /// <summary>One of <c>true</c>, <c>false</c>, <c>sql</c>, <c>correlation</c>.</summary>
    public required string FilterType { get; init; }

    /// <summary>SQL expression text (for <c>FilterType=sql</c>).</summary>
    public string? SqlExpression { get; init; }

    /// <summary>Correlation-filter fields (for <c>FilterType=correlation</c>).</summary>
    public CorrelationFilterFields? Correlation { get; init; }

    public RuleFilter ToFilter()
    {
        switch (FilterType?.ToLowerInvariant())
        {
            case "true": return TrueFilter.Instance;
            case "false": return FalseFilter.Instance;
            case "sql":
                if (string.IsNullOrWhiteSpace(SqlExpression))
                    throw new ArgumentException("SqlExpression is required when FilterType=sql.");
                return new SqlFilter(SqlExpression);
            case "correlation":
                if (Correlation is null)
                    throw new ArgumentException("Correlation payload is required when FilterType=correlation.");
                return new CorrelationFilter
                {
                    MessageId = Correlation.MessageId,
                    CorrelationId = Correlation.CorrelationId,
                    Subject = Correlation.Subject,
                    To = Correlation.To,
                    ReplyTo = Correlation.ReplyTo,
                    ReplyToSessionId = Correlation.ReplyToSessionId,
                    SessionId = Correlation.SessionId,
                    ContentType = Correlation.ContentType,
                    Properties = Correlation.Properties is null
                        ? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>(
                            Correlation.Properties.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)),
                            StringComparer.Ordinal),
                };
            default:
                throw new ArgumentException($"Unknown FilterType '{FilterType}'. Expected one of: true, false, sql, correlation.");
        }
    }
}

public sealed record CorrelationFilterFields(
    string? MessageId,
    string? CorrelationId,
    string? Subject,
    string? To,
    string? ReplyTo,
    string? ReplyToSessionId,
    string? SessionId,
    string? ContentType,
    Dictionary<string, string?>? Properties);

public sealed record TopicResponse(string Name, TimeSpan? DefaultMessageTimeToLive, EntityStatus Status)
{
    public static TopicResponse From(TopicDescriptor d) => new(d.Name, d.DefaultMessageTimeToLive, d.Status);
}

public sealed record SubscriptionResponse(
    string TopicName,
    string Name,
    int MaxDeliveryCount,
    TimeSpan LockDuration,
    bool DeadLetteringOnMessageExpiration,
    TimeSpan? DefaultMessageTimeToLive,
    bool RequiresSession,
    string? ForwardTo,
    string? ForwardDeadLetteredMessagesTo,
    EntityStatus Status,
    string BackingQueueName,
    long? ActiveMessageCount)
{
    public static SubscriptionResponse From(SubscriptionDescriptor d, long? count = null) => new(
        d.TopicName,
        d.Name,
        d.MaxDeliveryCount,
        d.LockDuration,
        d.DeadLetteringOnMessageExpiration,
        d.DefaultMessageTimeToLive,
        d.RequiresSession,
        d.ForwardTo,
        d.ForwardDeadLetteredMessagesTo,
        d.Status,
        d.BackingQueueName,
        count);
}

public sealed record RuleResponse(
    string TopicName,
    string SubscriptionName,
    string Name,
    string FilterType,
    string? SqlExpression,
    CorrelationFilterFields? Correlation)
{
    public static RuleResponse From(RuleDescriptor r) => r.Filter switch
    {
        TrueFilter => new(r.TopicName, r.SubscriptionName, r.Name, "true", null, null),
        FalseFilter => new(r.TopicName, r.SubscriptionName, r.Name, "false", null, null),
        SqlFilter sql => new(r.TopicName, r.SubscriptionName, r.Name, "sql", sql.Expression, null),
        CorrelationFilter c => new(r.TopicName, r.SubscriptionName, r.Name, "correlation", null,
            new CorrelationFilterFields(
                c.MessageId, c.CorrelationId, c.Subject, c.To, c.ReplyTo, c.ReplyToSessionId,
                c.SessionId, c.ContentType,
                c.Properties.Count == 0 ? null
                    : c.Properties.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()))),
        _ => new(r.TopicName, r.SubscriptionName, r.Name, "unknown", null, null),
    };
}
