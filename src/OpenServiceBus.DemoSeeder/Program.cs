using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenServiceBus.DemoSeeder;

var builder = Host.CreateApplicationBuilder(args);

string Env(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var options = new SeederOptions(
    ConnectionString: Env("SEEDER_CONNECTION",
        "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true"),
    ManagementUrl: Env("SEEDER_MGMT_URL", "http://localhost:5300"),
    ResetIntervalSeconds: int.TryParse(Env("SEEDER_RESET_INTERVAL_SECONDS", "1800"), out var r) ? r : 1800,
    MaxActive: int.TryParse(Env("SEEDER_MAX_ACTIVE", "450"), out var m) ? m : 450);

builder.Services.AddSingleton(options);
builder.Services.AddHostedService<Seeder>();

await builder.Build().RunAsync();
