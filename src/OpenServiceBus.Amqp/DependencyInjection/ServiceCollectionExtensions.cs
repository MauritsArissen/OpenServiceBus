using OpenServiceBus.Amqp.Diagnostics;
using OpenServiceBus.Amqp.Hosting;
using OpenServiceBus.Amqp.Lifecycle;
using OpenServiceBus.Amqp.Topics;
using OpenServiceBus.Amqp.WebSockets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Amqp.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OpenServiceBus AMQP listener as a hosted service.
    /// Configure via <see cref="AmqpListenerOptions"/> (bound from the "OpenServiceBus:Amqp" section if using configuration).
    /// </summary>
    public static IServiceCollection AddOpenServiceBusAmqp(
        this IServiceCollection services,
        Action<AmqpListenerOptions>? configure = null)
    {
        services.AddOptions<AmqpListenerOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRuleActionApplier, AmqpRuleActionApplier>();
        services.TryAddSingleton<AmqpListenerHost>();
        services.AddHostedService(sp => sp.GetRequiredService<AmqpListenerHost>());
        services.AddHostedService<TtlExpirationService>();
        services.AddHostedService<ScheduledMessageActivator>();
        services.AddHostedService<DiagnosticsHostedService>();

        // AMQP-over-WebSocket bridge. Stays off until either an Options binding sets Enabled
        // (e.g. the Host's "OpenServiceBus:WebSockets" config section) or the caller opts in via
        // AddOpenServiceBusAmqpWebSockets below. The hosted service early-returns when disabled,
        // so this registration is essentially free.
        services.AddOptions<WebSocketBridgeOptions>();
        services.AddHostedService<WebSocketBridgeService>();

        return services;
    }
}
