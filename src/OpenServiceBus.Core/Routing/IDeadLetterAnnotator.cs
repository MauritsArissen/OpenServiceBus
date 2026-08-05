namespace OpenServiceBus.Core.Routing;

/// <summary>
/// Stamps the Service Bus dead-letter metadata (<c>DeadLetterReason</c>,
/// <c>DeadLetterErrorDescription</c>, <c>x-opt-deadletter-source</c>) onto an encoded
/// message before it is moved to a dead-letter sub-entity. Lives in Core so the router can
/// annotate transfer dead-letter moves without a dependency on the AMQP codec; the
/// implementation ships with the AMQP layer.
/// </summary>
public interface IDeadLetterAnnotator
{
    byte[] Annotate(byte[] encodedMessage, string sourceEntity, string reason, string description);
}
