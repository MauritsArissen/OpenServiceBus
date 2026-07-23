using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using NovaBank.Api.Api;
using NovaBank.Api.Configuration;
using NovaBank.Api.Infrastructure;
using NovaBank.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

// ---- configuration ---------------------------------------------------------------------
// The ServiceBus section is layered from appsettings.json + appsettings.{Environment}.json.
// Run with --launch-profile Local (OpenServiceBus emulator) or Azure (real namespace).
builder.Services.AddOptions<ServiceBusOptions>()
    .BindConfiguration(ServiceBusOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
        "ServiceBus:ConnectionString is not configured. Start with --launch-profile Local " +
        "(dockerized OpenServiceBus) or --launch-profile Azure (real Azure Service Bus), " +
        "or set the ServiceBus__ConnectionString environment variable.")
    .ValidateOnStart();

// ---- services ----------------------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InMemoryBankStore>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;
    var clientOptions = new ServiceBusClientOptions();
    if (options.ClientRetryDelay is { } delay)
    {
        clientOptions.RetryOptions.Delay = delay;
    }
    return new ServiceBusClient(options.ConnectionString, clientOptions);
});
builder.Services.AddSingleton<BusSenders>();
builder.Services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();

// Message consumers. Everything below the API line is driven purely by Service Bus.
builder.Services.AddHostedService<TransferWorker>();
builder.Services.AddHostedService<PaymentWorker>();
builder.Services.AddHostedService<AuditWorker>();
builder.Services.AddHostedService<FraudWorker>();
builder.Services.AddHostedService<NotificationWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NovaBank API",
        Version = "v1",
        Description =
            "Event-driven demo bank. Transfers settle asynchronously via a duplicate-detected queue, " +
            "payments ride a session queue (per-account FIFO, broker-side scheduling), and every domain " +
            "event fans out over a topic to audit / fraud / notification subscriptions. The messaging " +
            "layer is plain Azure.Messaging.ServiceBus - point the connection string at Azure or at a " +
            "local OpenServiceBus container, nothing else changes.",
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "NovaBank v1");
    o.DocumentTitle = "NovaBank API";
});

CustomerEndpoints.Map(app);
AccountEndpoints.Map(app);
TransferEndpoints.Map(app);
PaymentEndpoints.Map(app);
OperationsEndpoints.Map(app);

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

SeedData.Apply(
    app.Services.GetRequiredService<InMemoryBankStore>(),
    app.Services.GetRequiredService<TimeProvider>());

app.Run();

// Exposes the entry point to WebApplicationFactory<Program> in the test project.
public partial class Program;
