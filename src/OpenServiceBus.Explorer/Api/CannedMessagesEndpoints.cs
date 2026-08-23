using OpenServiceBus.Explorer.CannedMessages;

namespace OpenServiceBus.Explorer.Api;

public static class CannedMessagesEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxImportBatch = 500;

    public static IEndpointRouteBuilder MapCannedMessagesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/canned");

        api.MapGet("", (CannedMessageLibrary library) => Results.Ok(library.List()));

        api.MapPost("", (CannedMessage message, CannedMessageLibrary library) =>
        {
            if (Invalid(message) is { } problem) return problem;
            return library.TryCreate(message)
                ? Results.Created($"/api/canned/{Uri.EscapeDataString(message.Name)}", message)
                : Results.Conflict(new { error = $"A canned message named '{message.Name}' already exists." });
        });

        api.MapPut("{name}", (string name, CannedMessage message, CannedMessageLibrary library) =>
        {
            if (Invalid(message) is { } problem) return problem;
            return library.Update(name, message) switch
            {
                UpdateResult.Updated => Results.Ok(message),
                UpdateResult.NotFound => Results.NotFound(new { error = $"No canned message named '{name}'." }),
                _ => Results.Conflict(new { error = $"A canned message named '{message.Name}' already exists." }),
            };
        });

        api.MapDelete("{name}", (string name, CannedMessageLibrary library) =>
            library.Delete(name) ? Results.NoContent() : Results.NotFound(new { error = $"No canned message named '{name}'." }));

        api.MapPost("{name}/duplicate", (string name, CannedMessageLibrary library) =>
            library.Duplicate(name) is { } copy
                ? Results.Ok(copy)
                : Results.NotFound(new { error = $"No canned message named '{name}'." }));

        api.MapPost("import", (ImportRequest request, CannedMessageLibrary library) =>
        {
            if (request.Messages is not { Count: > 0 }) return Results.BadRequest(new { error = "No messages to import." });
            if (request.Messages.Count > MaxImportBatch) return Results.BadRequest(new { error = $"Import is capped at {MaxImportBatch} messages." });
            foreach (var message in request.Messages)
            {
                if (Invalid(message) is { } problem) return problem;
            }

            var conflicts = library.ConflictsWith(request.Messages.Select(m => m.Name));
            if (conflicts.Count > 0 && request.ConflictMode is not ("replace" or "skip"))
            {
                return Results.Conflict(new { conflicts });
            }

            var summary = library.Import(request.Messages, request.ConflictMode == "replace");
            return Results.Ok(summary);
        });

        api.MapPost("reset", (CannedMessageLibrary library) =>
        {
            library.ResetToDefaults();
            return Results.Ok(library.List());
        });

        return endpoints;
    }

    private static IResult? Invalid(CannedMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Name))
        {
            return Results.BadRequest(new { error = "A canned message needs a non-empty name." });
        }
        if (message.Name.Length > MaxNameLength)
        {
            return Results.BadRequest(new { error = $"Name is capped at {MaxNameLength} characters." });
        }
        return null;
    }
}

public sealed record ImportRequest(List<CannedMessage>? Messages, string? ConflictMode);
