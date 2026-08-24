using OpenServiceBus.Explorer.Environments;

namespace OpenServiceBus.Explorer.Api;

public static class EnvironmentsEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxImportBatch = 200;

    public static IEndpointRouteBuilder MapEnvironmentsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/environments");

        api.MapGet("", (EnvironmentLibrary library) => Results.Ok(library.List()));

        api.MapPost("", (ExplorerEnvironment environment, EnvironmentLibrary library) =>
        {
            if (Invalid(environment) is { } problem) return problem;
            return library.TryCreate(Normalize(environment))
                ? Results.Created($"/api/environments/{Uri.EscapeDataString(environment.Name)}", environment)
                : Results.Conflict(new { error = $"An environment named '{environment.Name}' already exists." });
        });

        api.MapPut("{name}", (string name, ExplorerEnvironment environment, EnvironmentLibrary library) =>
        {
            if (Invalid(environment) is { } problem) return problem;
            return library.Update(name, Normalize(environment)) switch
            {
                UpdateOutcome.Updated => Results.Ok(environment),
                UpdateOutcome.NotFound => Results.NotFound(new { error = $"No environment named '{name}'." }),
                _ => Results.Conflict(new { error = $"An environment named '{environment.Name}' already exists." }),
            };
        });

        api.MapDelete("{name}", (string name, EnvironmentLibrary library) =>
            library.Delete(name) ? Results.NoContent() : Results.NotFound(new { error = $"No environment named '{name}'." }));

        api.MapPost("{name}/duplicate", (string name, EnvironmentLibrary library) =>
            library.Duplicate(name) is { } copy
                ? Results.Ok(copy)
                : Results.NotFound(new { error = $"No environment named '{name}'." }));

        api.MapPost("import", (EnvironmentImportRequest request, EnvironmentLibrary library) =>
        {
            if (request.Environments is not { Count: > 0 }) return Results.BadRequest(new { error = "No environments to import." });
            if (request.Environments.Count > MaxImportBatch) return Results.BadRequest(new { error = $"Import is capped at {MaxImportBatch} environments." });
            foreach (var environment in request.Environments)
            {
                if (Invalid(environment) is { } problem) return problem;
            }

            var conflicts = library.ConflictsWith(request.Environments.Select(e => e.Name));
            if (conflicts.Count > 0 && request.ConflictMode is not ("replace" or "skip"))
            {
                return Results.Conflict(new { conflicts });
            }

            var (added, replaced, skipped) = library.Import(
                request.Environments.Select(Normalize).ToList(), request.ConflictMode == "replace");
            return Results.Ok(new { added, replaced, skipped });
        });

        api.MapGet("export", (EnvironmentLibrary library) => Results.File(
            System.Text.Encoding.UTF8.GetBytes(EnvironmentLibrary.Serialize(library.List())),
            "application/json",
            "environments.json"));

        api.MapPost("reset", (EnvironmentLibrary library) =>
        {
            library.ResetToDefaults();
            return Results.Ok(library.List());
        });

        return endpoints;
    }

    private static ExplorerEnvironment Normalize(ExplorerEnvironment environment) =>
        environment with { Values = environment.Values ?? [] };

    private static IResult? Invalid(ExplorerEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(environment.Name))
        {
            return Results.BadRequest(new { error = "An environment needs a non-empty name." });
        }
        if (environment.Name.Length > MaxNameLength)
        {
            return Results.BadRequest(new { error = $"Name is capped at {MaxNameLength} characters." });
        }
        return null;
    }
}

public sealed record EnvironmentImportRequest(List<ExplorerEnvironment>? Environments, string? ConflictMode);
