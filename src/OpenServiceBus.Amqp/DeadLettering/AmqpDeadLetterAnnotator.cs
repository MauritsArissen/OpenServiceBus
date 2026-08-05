using OpenServiceBus.Core.Routing;

namespace OpenServiceBus.Amqp.DeadLettering;

/// <summary>
/// <see cref="IDeadLetterAnnotator"/> backed by the AMQP codec: decodes the stored message,
/// stamps <c>DeadLetterReason</c>/<c>DeadLetterErrorDescription</c> and
/// <c>x-opt-deadletter-source</c>, and re-encodes. Injected into the router so transfer
/// dead-letter moves carry the same metadata as regular dead-lettering.
/// </summary>
public sealed class AmqpDeadLetterAnnotator : IDeadLetterAnnotator
{
    public byte[] Annotate(byte[] encodedMessage, string sourceEntity, string reason, string description) =>
        DeadLetterEncoder.AppendDeadLetterHeaders(encodedMessage, sourceEntity, reason, description);
}
