using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Management.Endpoints;

public static class PurgeEndpoints
{
    /// <summary>
    /// Maps the emulator-native purge surface (issue #36). Real Azure Service Bus has no
    /// purge API; these endpoints exist so long-lived test brokers can clear messages
    /// between test cases without recreating entities. Topology, entity settings, and
    /// live links are untouched.
    /// </summary>
    public static IEndpointRouteBuilder MapPurgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDelete("/queues/{name}/messages", async (
            string name, string? subqueue, IQueueRegistry queues, IMessageStore store, ITopicRegistry? topics, CancellationToken ct) =>
        {
            if (!TryParseSubqueue(subqueue, out var deadLetterOnly))
            {
                return Results.BadRequest(new { error = $"Unknown subqueue '{subqueue}'. Supported: 'deadletter'." });
            }
            var purged = await new EntityPurger(queues, store, topics).PurgeQueueAsync(name, deadLetterOnly, ct);
            return purged is null ? Results.NotFound() : Results.Ok(new PurgeResponse(purged.Value));
        });

        endpoints.MapDelete("/topics/{name}/messages", async (
            string name, IQueueRegistry queues, IMessageStore store, ITopicRegistry? topics, CancellationToken ct) =>
        {
            var purged = await new EntityPurger(queues, store, topics).PurgeTopicAsync(name, ct);
            return purged is null ? Results.NotFound() : Results.Ok(new PurgeResponse(purged.Value));
        });

        endpoints.MapDelete("/topics/{topic}/subscriptions/{name}/messages", async (
            string topic, string name, string? subqueue, IQueueRegistry queues, IMessageStore store, ITopicRegistry? topics, CancellationToken ct) =>
        {
            if (!TryParseSubqueue(subqueue, out var deadLetterOnly))
            {
                return Results.BadRequest(new { error = $"Unknown subqueue '{subqueue}'. Supported: 'deadletter'." });
            }
            var purged = await new EntityPurger(queues, store, topics).PurgeSubscriptionAsync(topic, name, deadLetterOnly, ct);
            return purged is null ? Results.NotFound() : Results.Ok(new PurgeResponse(purged.Value));
        });

        endpoints.MapPost("/purge", async (
            IQueueRegistry queues, IMessageStore store, ITopicRegistry? topics, CancellationToken ct) =>
        {
            var (purged, entities) = await new EntityPurger(queues, store, topics).PurgeAllAsync(ct);
            return Results.Ok(new PurgeAllResponse(purged, entities));
        });

        return endpoints;
    }

    private static bool TryParseSubqueue(string? subqueue, out bool deadLetterOnly)
    {
        deadLetterOnly = string.Equals(subqueue, "deadletter", StringComparison.OrdinalIgnoreCase);
        return deadLetterOnly || string.IsNullOrEmpty(subqueue);
    }

    public sealed record PurgeResponse(long Purged);

    public sealed record PurgeAllResponse(long Purged, int Entities);
}
