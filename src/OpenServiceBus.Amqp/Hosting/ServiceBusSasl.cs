using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Sasl;
using Amqp.Types;

namespace OpenServiceBus.Amqp.Hosting;

/// <summary>
/// Enables the SASL mechanisms an Azure Service Bus client expects:
///   <c>ANONYMOUS</c>  - generic AMQP clients and rhea-based SDKs (Node.js, Python) use this.
///   <c>MSSBCBS</c>    - Azure SDK / Service Bus protocol stack always uses this in emulator mode;
///                       at the SASL layer it is a no-op handshake (real auth happens via $cbs put-token).
/// </summary>
internal static class ServiceBusSasl
{
    public static void ConfigureListenerMechanisms(ConnectionListener listener)
    {
        // Both mechanisms use OUR no-op profile (not AMQPNetLite's built-in anonymous one)
        // so the de-pipelining pause in UpgradeTransport applies to every client.
        listener.SASL.EnableMechanism(new Symbol("ANONYMOUS"), new NoOpSaslProfile("ANONYMOUS"));
        listener.SASL.EnableMechanism(new Symbol("MSSBCBS"), new NoOpSaslProfile("MSSBCBS"));
    }

    /// <summary>
    /// Accept-anything SASL profile with one deliberate quirk: a short pause before the
    /// transport upgrades to AMQP.
    ///
    /// Why: rhea (the AMQP stack under the Node.js and Python Azure SDKs) has a handoff bug -
    /// any bytes that arrive in the SAME TCP segment as the sasl-outcome are peeked but never
    /// processed after the SASL layer hands the socket to the AMQP transport. When the broker
    /// pipelines its AMQP header + open behind the outcome (AMQPNetLite's default), the client
    /// randomly stalls for its full 60s init timeout depending on how the OS coalesced the
    /// writes. The pause runs AFTER the outcome is written and BEFORE the header, giving the
    /// outcome its own segment - same observable behavior as real Azure, which does not
    /// pipeline its open.
    /// </summary>
    private sealed class NoOpSaslProfile : SaslProfile
    {
        public NoOpSaslProfile(string mechanism) : base(new Symbol(mechanism))
        {
        }

        protected override ITransport UpgradeTransport(ITransport transport)
        {
            Thread.Sleep(50);
            return transport;
        }

        protected override DescribedList GetStartCommand(string hostname) =>
            throw new NotSupportedException("Server-side profile only.");

        protected override DescribedList OnCommand(DescribedList command)
        {
            if (command.Descriptor.Code == 0x41) // sasl-init
            {
                return new SaslOutcome { Code = SaslCode.Ok };
            }
            throw new AmqpException(ErrorCode.NotAllowed, $"Unexpected SASL command {command.Descriptor.Name}.");
        }
    }
}
