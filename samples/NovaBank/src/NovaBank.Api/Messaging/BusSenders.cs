using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using NovaBank.Api.Configuration;

namespace NovaBank.Api.Messaging;

/// <summary>One long-lived sender per entity, shared app-wide (senders are thread-safe).</summary>
public sealed class BusSenders : IAsyncDisposable
{
    public BusSenders(ServiceBusClient client, IOptions<ServiceBusOptions> options)
    {
        Transfers = client.CreateSender(options.Value.TransfersQueue);
        Payments = client.CreateSender(options.Value.PaymentsQueue);
        Events = client.CreateSender(options.Value.EventsTopic);
    }

    public ServiceBusSender Transfers { get; }
    public ServiceBusSender Payments { get; }
    public ServiceBusSender Events { get; }

    public async ValueTask DisposeAsync()
    {
        await Transfers.DisposeAsync();
        await Payments.DisposeAsync();
        await Events.DisposeAsync();
    }
}
