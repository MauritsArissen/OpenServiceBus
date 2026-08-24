using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenServiceBus.Explorer.Tests;

/// <summary>
/// OSB_EXPLORER_ENVIRONMENTS_FILE: the same committable-file lifecycle as the canned
/// message library - load at startup, write-back on mutation, reset re-reading the disk.
/// </summary>
public class EnvironmentFilePersistenceTests
{
    private static WebApplicationFactory<Program> FactoryFor(string path) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("OSB_EXPLORER_ENVIRONMENTS_FILE", path));

    private static string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"envs-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private const string SeedJson = """
        [
          { "name": "Card of Alice", "values": [ { "key": "cardnumber", "value": "123400000", "enabled": true } ] }
        ]
        """;

    [Fact]
    public async Task Startup_LoadsEnvironmentsFromTheConfiguredFile()
    {
        var path = TempFile(SeedJson);
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            var list = JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray();
            list.Count.ShouldBe(1);
            list[0]!["name"]!.GetValue<string>().ShouldBe("Card of Alice");

            var config = JsonNode.Parse(await http.GetStringAsync("/api/config"))!;
            config["environmentsFile"]!["configured"]!.GetValue<bool>().ShouldBeTrue();
            config["environmentsFile"]!["writable"]!.GetValue<bool>().ShouldBeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Mutations_WriteBack_AndResetReloadsExternalEdits()
    {
        var path = TempFile(SeedJson);
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            (await http.PostAsJsonAsync("/api/environments", new
            {
                name = "Card of Bob",
                values = new object[] { new { key = "cardnumber", value = "128948000", enabled = true } },
            })).StatusCode.ShouldBe(HttpStatusCode.Created);
            JsonNode.Parse(File.ReadAllText(path))!.AsArray().Count.ShouldBe(2);

            File.WriteAllText(path, """
                [ { "name": "Pulled In", "values": [] } ]
                """);
            (await http.PostAsync("/api/environments/reset", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

            var list = JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray();
            list.Count.ShouldBe(1);
            list[0]!["name"]!.GetValue<string>().ShouldBe("Pulled In");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InvalidJsonFile_StartsEmptyInsteadOfCrashing()
    {
        var path = TempFile("nope {");
        try
        {
            await using var factory = FactoryFor(path);
            using var http = factory.CreateClient();

            JsonNode.Parse(await http.GetStringAsync("/api/environments"))!.AsArray().Count.ShouldBe(0);
        }
        finally { File.Delete(path); }
    }
}
