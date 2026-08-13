namespace OpenServiceBus.Amqp.Routing;

/// <summary>Service Bus AMQP error conditions the official SDKs map to typed exceptions.</summary>
public static class ServiceBusErrors
{
    /// <summary>Maps to <c>ServiceBusFailureReason.MessagingEntityDisabled</c> in the SDKs.</summary>
    public const string EntityDisabled = "com.microsoft:entity-disabled";

    /// <summary>Maps to <c>ServiceBusFailureReason.MessageSizeExceeded</c> in the SDKs.</summary>
    public const string MessageSizeExceeded = "amqp:link:message-size-exceeded";

    /// <summary>Maps to <c>ServiceBusFailureReason.QuotaExceeded</c> in the SDKs.</summary>
    public const string QuotaExceeded = "amqp:resource-limit-exceeded";

    /// <summary>Maps to <c>ServiceBusFailureReason.MessageLockLost</c> in the SDKs.</summary>
    public const string MessageLockLost = "com.microsoft:message-lock-lost";

    /// <summary>Maps to <c>ServiceBusFailureReason.SessionLockLost</c> in the SDKs.</summary>
    public const string SessionLockLost = "com.microsoft:session-lock-lost";
}
