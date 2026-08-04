using OpenServiceBus.Explorer.Api;
using OpenServiceBus.Explorer.Metrics;
using OpenServiceBus.Explorer.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsCollector>());
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapExplorerEndpoints();
app.MapAdminEndpoints();

await app.RunAsync();

public partial class Program;
