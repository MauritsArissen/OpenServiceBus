using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Messaging;
using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Management.Endpoints;

public static class CannedMessagesEndpoints
{
    /// <summary>
    /// Maps the REST surface for canned messages entity CRUD.
    /// </summary>
    public static IEndpointRouteBuilder MapCannedMessagesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/canned-messages");

        group.MapGet("/", async (ICannedMessagesRegistry registry, CancellationToken ct) =>
        {
            var cannedMessages = await registry.ListAsync(ct);
            return Results.Ok(cannedMessages);
        });

        group.MapGet("/{name}", async (string name, ICannedMessagesRegistry registry, CancellationToken ct) =>
        {
            var cannedMessage = await registry.GetAsync(name, ct);
            if (cannedMessage is null) return Results.NotFound();
            return Results.Ok(cannedMessage);
        });

        group.MapPut("/{name}", async (string name, CannedMessage cannedMessage, ICannedMessagesRegistry registry, CancellationToken ct) =>
        {
            try
            {
                var existing = await registry.GetAsync(name, ct);
                if (existing is not null)
                {
                    existing.TopicOrQueue = cannedMessage.TopicOrQueue;
                    existing.Message = cannedMessage.Message;
                    var updated = await registry.UpdateAsync(cannedMessage, ct);
                    return Results.Ok(updated);
                }

                var created = await registry.CreateAsync(cannedMessage, ct);
                return Results.Ok(created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{name}", async (string name, ICannedMessagesRegistry registry, CancellationToken ct) =>
        {
            var deleted = await registry.DeleteAsync(name, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }
}
