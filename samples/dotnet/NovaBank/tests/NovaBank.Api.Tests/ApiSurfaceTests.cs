using Shouldly;

namespace NovaBank.Api.Tests;

public class ApiSurfaceTests : IClassFixture<NovaBankAppFixture>
{
    private readonly NovaBankAppFixture _app;

    public ApiSurfaceTests(NovaBankAppFixture app) => _app = app;

    [Fact]
    public async Task SwaggerDocument_IsServed()
    {
        var response = await _app.Client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        json.ShouldContain("NovaBank API");
        json.ShouldContain("/api/transfers");
        json.ShouldContain("/api/payments");
        json.ShouldContain("/api/admin/dead-letters/{queueName}");
    }

    [Fact]
    public async Task HealthEndpoint_Responds()
    {
        var response = await _app.Client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }
}
