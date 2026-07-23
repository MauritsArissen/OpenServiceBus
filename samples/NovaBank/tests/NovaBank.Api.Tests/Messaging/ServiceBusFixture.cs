using Azure.Messaging.ServiceBus;
using NovaBank.Api.Configuration;
using OpenServiceBus.Testing;

namespace NovaBank.Api.Tests.Messaging;

/// <summary>
/// Bus-level fixture: an embedded OpenServiceBus broker + a raw ServiceBusClient and the
/// NovaBank topology - but NO web app and NO hosted workers. Tests in this layer exercise
/// the messaging components and broker semantics directly, and can assert on broker
/// internals through <see cref="OpenServiceBusTestHost.Store"/>.
/// </summary>
public sealed class ServiceBusFixture : IAsyncLifetime
{
    public OpenServiceBusTestHost Bus { get; private set; } = null!;
    public ServiceBusClient Client { get; private set; } = null!;
    public ServiceBusOptions Names { get; } = new();

    public async Task InitializeAsync()
    {
        Bus = await OpenServiceBusTestHost.StartAsync();
        await NovaBankTopology.CreateAsync(Bus);
        Client = new ServiceBusClient(Bus.ConnectionString, new ServiceBusClientOptions
        {
            // Keep retry back-off short: accept/receive polling against the emulator fails
            // fast, so the default 0.8s delay would dominate test wall time.
            RetryOptions = new ServiceBusRetryOptions { Delay = TimeSpan.FromMilliseconds(100) },
        });
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        await Bus.DisposeAsync();
    }

    /// <summary>Receive messages from a receiver until one matches, tolerating unrelated
    /// traffic from earlier tests in the same class. Matched-or-not, everything is completed
    /// so reruns stay clean.</summary>
    public static async Task<ServiceBusReceivedMessage?> ReceiveUntilAsync(
        ServiceBusReceiver receiver,
        Func<ServiceBusReceivedMessage, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500));
            if (msg is null) continue;
            await receiver.CompleteMessageAsync(msg);
            if (predicate(msg)) return msg;
        }
        return null;
    }
}
