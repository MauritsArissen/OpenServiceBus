using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Management.Endpoints;

public static class QueueEndpoints
{
    /// <summary>
    /// Maps the REST surface for queue entity CRUD under <c>/queues</c>.
    /// Shape is kept close to the official emulator's HTTP management surface where it overlaps;
    /// full config.json compatibility lands.
    /// </summary>
    public static IEndpointRouteBuilder MapQueueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/queues");

        group.MapGet("/", async (IQueueRegistry registry, IMessageStore store, CancellationToken ct) =>
        {
            var queues = await registry.ListAsync(ct);
            var withCounts = new List<QueueResponse>(queues.Count);
            foreach (var q in queues)
            {
                var count = await store.CountAsync(q.Name, ct);
                var (enqueued, completed) = store.LifetimeCounters(q.Name);
                withCounts.Add(QueueResponse.From(q, count, enqueued, completed));
            }
            return Results.Ok(withCounts);
        });

        group.MapGet("/{name}", async (string name, IQueueRegistry registry, IMessageStore store, CancellationToken ct) =>
        {
            var queue = await registry.GetAsync(name, ct);
            if (queue is null) return Results.NotFound();
            var count = await store.CountAsync(name, ct);
            var (enqueued, completed) = store.LifetimeCounters(name);
            return Results.Ok(QueueResponse.From(queue, count, enqueued, completed));
        });

        group.MapPut("/{name}", async (string name, CreateQueueRequest? body, IQueueRegistry registry, CancellationToken ct) =>
        {
            var descriptor = (body ?? new CreateQueueRequest()).ToDescriptor(name);
            try
            {
                var created = await registry.CreateAsync(descriptor, ct);
                return Results.Ok(QueueResponse.From(created));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{name}", async (string name, IQueueRegistry registry, CancellationToken ct) =>
        {
            var deleted = await registry.DeleteAsync(name, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }
}

public sealed record CreateQueueRequest
{
    public int MaxDeliveryCount { get; init; } = 10;
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(60);
    public bool DeadLetteringOnMessageExpiration { get; init; }
    public TimeSpan? DefaultMessageTimeToLive { get; init; }
    public bool RequiresSession { get; init; }
    public bool RequiresDuplicateDetection { get; init; }
    public TimeSpan? DuplicateDetectionHistoryTimeWindow { get; init; }
    public string? ForwardTo { get; init; }
    public string? ForwardDeadLetteredMessagesTo { get; init; }

    public QueueDescriptor ToDescriptor(string name) => new()
    {
        Name = name,
        MaxDeliveryCount = MaxDeliveryCount,
        LockDuration = LockDuration,
        DeadLetteringOnMessageExpiration = DeadLetteringOnMessageExpiration,
        DefaultMessageTimeToLive = DefaultMessageTimeToLive,
        RequiresSession = RequiresSession,
        RequiresDuplicateDetection = RequiresDuplicateDetection,
        DuplicateDetectionHistoryTimeWindow = DuplicateDetectionHistoryTimeWindow,
        ForwardTo = ForwardTo,
        ForwardDeadLetteredMessagesTo = ForwardDeadLetteredMessagesTo,
    };
}

public sealed record QueueResponse(
    string Name,
    int MaxDeliveryCount,
    TimeSpan LockDuration,
    bool DeadLetteringOnMessageExpiration,
    TimeSpan? DefaultMessageTimeToLive,
    bool RequiresSession,
    bool RequiresDuplicateDetection,
    TimeSpan? DuplicateDetectionHistoryTimeWindow,
    string? ForwardTo,
    string? ForwardDeadLetteredMessagesTo,
    long? ActiveMessageCount,
    long? EnqueuedCount = null,
    long? CompletedCount = null)
{
    public static QueueResponse From(QueueDescriptor d, long? count = null, long? enqueued = null, long? completed = null) => new(
        d.Name,
        d.MaxDeliveryCount,
        d.LockDuration,
        d.DeadLetteringOnMessageExpiration,
        d.DefaultMessageTimeToLive,
        d.RequiresSession,
        d.RequiresDuplicateDetection,
        d.DuplicateDetectionHistoryTimeWindow,
        d.ForwardTo,
        d.ForwardDeadLetteredMessagesTo,
        count,
        enqueued,
        completed);
}
