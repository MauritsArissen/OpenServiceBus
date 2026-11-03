namespace OpenServiceBus.Amqp.Hosting;

public sealed class AmqpListenerOptions
{
    public string Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 5672;

    public string ContainerId { get; set; } = "OpenServiceBus";

    /// <summary>
    /// Idle timeout advertised on the outgoing AMQP Open frame, in milliseconds.
    /// AMQPNetLite issue #238 means the listener does not currently enforce dead-client detection
    /// purely from this value; advertising it still lets clients drive their own keep-alive logic.
    /// </summary>
    public uint IdleTimeoutMs { get; set; } = 30_000;

    public uint MaxFrameSize { get; set; } = 256 * 1024;

    /// <summary>
    /// Sessions allowed per AMQP connection (the Open frame's channel-max, plus one). The
    /// Azure SDK opens a session per sender/receiver link, so AMQPNetLite's default of 256
    /// capped one ServiceBusClient at ~255 concurrent links, failing attach 256+ with
    /// QuotaExceeded - a ceiling real Service Bus does not have (issue #93). Default 5000
    /// matches the service's concurrent-receiver headroom.
    /// </summary>
    public int MaxSessionsPerConnection { get; set; } = 5000;

    /// <summary>
    /// Links allowed per AMQP session (the Begin frame's handle-max, plus one). See
    /// <see cref="MaxSessionsPerConnection"/>; raised in the same spirit for clients that
    /// multiplex many links over one session.
    /// </summary>
    public int MaxLinksPerSession { get; set; } = 5000;

    /// <summary>
    /// Maximum message size advertised on link attach, in bytes. The Azure SDK reads this on
    /// link attach and rejects sends if it remains 0/unset (interprets as -1, refusing every message).
    /// Default 256 KB matches Azure Service Bus Standard tier.
    /// </summary>
    public ulong MaxMessageSize { get; set; } = 256 * 1024;

    /// <summary>
    /// Enable AMQPNetLite frame-level tracing routed through our logger at Debug level.
    /// Off by default - useful when diagnosing wire-protocol interop issues with new clients.
    /// </summary>
    public bool EnableFrameTracing { get; set; }

    /// <summary>
    /// When true, the broker validates SAS tokens at <c>$cbs put-token</c> against
    /// <see cref="SasKeys"/>. Off by default to preserve emulator-mode permissive auth.
    /// </summary>
    public bool RequireSasAuth { get; set; }

    /// <summary>
    /// Shared access keys recognised by <c>$cbs</c> when <see cref="RequireSasAuth"/> is enabled.
    /// Key = name (e.g. <c>RootManageSharedAccessKey</c>); value = the raw key string clients
    /// use in their connection-string <c>SharedAccessKey</c>.
    /// </summary>
    public Dictionary<string, string> SasKeys { get; set; } = new(StringComparer.Ordinal);
}
