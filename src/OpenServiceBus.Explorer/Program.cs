using OpenServiceBus.Explorer.Api;
using OpenServiceBus.Explorer.CannedMessages;
using OpenServiceBus.Explorer.Metrics;
using OpenServiceBus.Explorer.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<CannedMessageFileStore>();
builder.Services.AddSingleton<CannedMessageLibrary>(sp =>
    new CannedMessageLibrary(sp.GetRequiredService<CannedMessageFileStore>()));
builder.Services.AddHostedService<DemoLibraryResetService>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsCollector>());
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapExplorerEndpoints();
app.MapAdminEndpoints();
app.MapCannedMessagesEndpoints();

await app.RunAsync();

public partial class Program;
