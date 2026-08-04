namespace OpenServiceBus.Amqp.Routing;

/// <summary>Service Bus AMQP error conditions the official SDKs map to typed exceptions.</summary>
public static class ServiceBusErrors
{
    /// <summary>Maps to <c>ServiceBusFailureReason.MessagingEntityDisabled</c> in the SDKs.</summary>
    public const string EntityDisabled = "com.microsoft:entity-disabled";
}
