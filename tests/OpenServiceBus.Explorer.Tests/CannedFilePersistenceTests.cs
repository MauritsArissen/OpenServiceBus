using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// The OSB_EXPLORER_CANNED_FILE backing: load at startup, write-back on every mutation,
/// export in the file format, reset re-reading external edits, and graceful degradation
/// for read-only or invalid files. Without the setting the library stays in-memory
/// (covered by CannedMessagesEndpointTests).
/// </summary>
public class CannedFilePersistenceTests
{
    private static WebApplicationFactory<Program> FactoryFor(string path) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("OSB_EXPLORER_CANNED_FILE", path));

    private static string TempFile(string? content = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"canned-{Guid.NewGuid():N}.json");
        if (content is not null) File.WriteAllText(path, content);
        return path;
    }

    private const string SeedJson = """
        [
          { "name": "from-file", "targetEntity": "*", "body": "hello from disk", "count": 1, "strategy": "ATONCE" }
        ]
        """;

    [Fact]
    public async Task Startup_LoadsTheLibraryFromTheConfiguredFile()
    {
        var path = TempFile(SeedJson);
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            var list = JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray();
            list.Count.ShouldBe(1);
            list[0]!["name"]!.GetValue<string>().ShouldBe("from-file");
            list[0]!["body"]!.GetValue<string>().ShouldBe("hello from disk");

            var config = JsonNode.Parse(await http.GetStringAsync("/api/config"))!;
            config["cannedFile"]!["configured"]!.GetValue<bool>().ShouldBeTrue();
            config["cannedFile"]!["writable"]!.GetValue<bool>().ShouldBeTrue();
            config["cannedFile"]!["path"]!.GetValue<string>().ShouldBe(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Mutations_WriteBackToTheFile()
    {
        var path = TempFile(SeedJson);
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            (await http.PostAsJsonAsync("/api/canned", new { name = "added", body = "new" }))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            var afterCreate = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            afterCreate.Select(n => n!["name"]!.GetValue<string>()).ShouldBe(["added", "from-file"]);

            (await http.DeleteAsync("/api/canned/from-file")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            var afterDelete = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            afterDelete.Select(n => n!["name"]!.GetValue<string>()).ShouldBe(["added"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Export_MatchesTheFileFormatAndRoundTripsThroughImport()
    {
        var path = TempFile(SeedJson);
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();
            await http.PostAsJsonAsync("/api/canned", new { name = "second", body = "{{$guid}}" });

            var response = await http.GetAsync("/api/canned/export");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("canned-messages.json");
            var exported = await response.Content.ReadAsStringAsync();

            exported.ShouldBe(File.ReadAllText(path), "the export IS the file format");

            await using var fresh = new WebApplicationFactory<Program>();
            using var freshHttp = fresh.CreateClient();
            var import = await freshHttp.PostAsJsonAsync("/api/canned/import", new
            {
                messages = JsonNode.Parse(exported),
                conflictMode = "replace",
            });
            import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());
            var reimported = JsonNode.Parse(await freshHttp.GetStringAsync("/api/canned"))!.AsArray();
            reimported.Select(n => n!["name"]!.GetValue<string>()).ShouldBe(["from-file", "second"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reset_ReloadsWhatIsOnDiskRightNow()
    {
        var path = TempFile(SeedJson);
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();
            await http.PostAsJsonAsync("/api/canned", new { name = "session-only", body = "temp" });

            // Simulate a git pull changing the committed library file out from under the app.
            File.WriteAllText(path, """
                [ { "name": "pulled-in", "targetEntity": "*", "body": "fresh from git" } ]
                """);

            (await http.PostAsync("/api/canned/reset", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

            var list = JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray();
            list.Count.ShouldBe(1);
            list[0]!["name"]!.GetValue<string>().ShouldBe("pulled-in");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadOnlyFile_LoadsButKeepsEditsInMemory()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = TempFile(SeedJson);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            var config = JsonNode.Parse(await http.GetStringAsync("/api/config"))!;
            config["cannedFile"]!["writable"]!.GetValue<bool>().ShouldBeFalse();

            (await http.PostAsJsonAsync("/api/canned", new { name = "memory-only", body = "x" }))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray().Count.ShouldBe(2);
            JsonNode.Parse(File.ReadAllText(path))!.AsArray().Count.ShouldBe(1, "a read-only file must never be touched");
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidJsonFile_StartsEmptyInsteadOfCrashing()
    {
        var path = TempFile("this is not json {");
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray().Count.ShouldBe(0);
            (await http.PostAsJsonAsync("/api/canned", new { name = "recovers", body = "x" }))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingFile_StartsEmptyAndCreatesItOnFirstSave()
    {
        var path = TempFile();
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            JsonNode.Parse(await http.GetStringAsync("/api/canned"))!.AsArray().Count.ShouldBe(0);
            File.Exists(path).ShouldBeFalse("the file must not be created until something is saved");

            await http.PostAsJsonAsync("/api/canned", new { name = "first", body = "x" });
            File.Exists(path).ShouldBeTrue();
            JsonNode.Parse(File.ReadAllText(path))!.AsArray().Count.ShouldBe(1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
