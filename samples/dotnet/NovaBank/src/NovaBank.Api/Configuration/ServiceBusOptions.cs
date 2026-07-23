namespace NovaBank.Api.Configuration;

/// <summary>
/// Everything the app knows about Service Bus. The connection string is the only value that
/// differs between environments (appsettings.Local.json → OpenServiceBus emulator,
/// appsettings.Azure.json → a real Azure Service Bus namespace); entity names stay identical
/// so the messaging code is byte-for-byte the same everywhere.
/// </summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for the SDK's retry back-off delay (default 0.8s). Against the local
    /// emulator a short delay makes session pickup near-instant, because accept-next-session
    /// fails fast there and the SDK sleeps this long between attempts; against real Azure the
    /// server holds the accept open, so leave the default in place.
    /// </summary>
    public TimeSpan? ClientRetryDelay { get; set; }

    /// <summary>Transfer commands. Duplicate detection on MessageId gives at-most-once execution.</summary>
    public string TransfersQueue { get; set; } = "nova-transfers";

    /// <summary>Payment instructions. Session-enabled: SessionId = accountId → per-account FIFO.</summary>
    public string PaymentsQueue { get; set; } = "nova-payments";

    /// <summary>Domain events fan out from here to the subscriptions below.</summary>
    public string EventsTopic { get; set; } = "nova-events";

    public string AuditSubscription { get; set; } = "audit";
    public string FraudSubscription { get; set; } = "fraud";
    public string NotificationsSubscription { get; set; } = "notifications";
}
