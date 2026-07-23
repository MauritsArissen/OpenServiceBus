using System.Reflection;
using Amqp;
using Amqp.Framing;
using Amqp.Handler;
using Amqp.Types;
using Amqp.Listener;

namespace OpenServiceBus.Amqp.Hosting;

/// <summary>
/// Per-connection handler that hooks two points in the AMQPNetLite pipeline:
///
/// <para><b>ConnectionLocalOpen</b> - stamp container-id, idle-timeout, max-frame-size on the
/// outgoing AMQP Open frame. Workaround for AMQPNetLite #238 which means these are not
/// configurable directly on <see cref="ConnectionListener"/>.</para>
///
/// <para><b>SendDelivery</b> - when an outgoing delivery carries a <see cref="ReceiveContext"/>
/// in its UserToken (the IMessageSource path), copy its peek-lock token into the AMQP
/// <c>delivery-tag</c>. The Azure SDK's <c>ServiceBusReceiver.CompleteMessageAsync</c>
/// rejects messages with empty lock tokens - a non-Guid delivery-tag round-trips as
/// <see cref="Guid.Empty"/> and Complete throws InvalidOperationException.</para>
/// </summary>
internal sealed class ListenerEventHandler : IHandler
{
    // Delivery.Tag and Delivery.UserToken are internal in AMQPNetLite; reflect once at startup.
    private static readonly Type DeliveryType =
        typeof(Connection).Assembly.GetType("Amqp.Delivery", throwOnError: true)!;

    private static readonly PropertyInfo DeliveryTagProperty =
        DeliveryType.GetProperty("Tag", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("AMQPNetLite Delivery.Tag not found - upstream API changed.");

    private static readonly PropertyInfo DeliveryUserTokenProperty =
        DeliveryType.GetProperty("UserToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("AMQPNetLite Delivery.UserToken not found - upstream API changed.");

    private readonly uint _idleTimeoutMs;
    private readonly uint _maxFrameSize;
    private readonly string _containerId;

    public ListenerEventHandler(string containerId, uint idleTimeoutMs, uint maxFrameSize)
    {
        _containerId = containerId;
        _idleTimeoutMs = idleTimeoutMs;
        _maxFrameSize = maxFrameSize;
    }

    public bool CanHandle(EventId id) =>
        id == EventId.ConnectionLocalOpen || id == EventId.SessionLocalOpen ||
        id == EventId.LinkLocalOpen || id == EventId.SendDelivery;

    public void Handle(Event protocolEvent)
    {
        if (protocolEvent.Id == EventId.ConnectionLocalOpen && protocolEvent.Context is Open open)
        {
            open.ContainerId = _containerId;
            open.IdleTimeOut = _idleTimeoutMs;
            open.MaxFrameSize = _maxFrameSize;
            // Always populate the open frame's trailing `properties` field (list index 9).
            // pyamqp (the Python SDK's AMQP stack) indexes open frames as frame[9] and
            // crashes with IndexError on a short field list - AMQP permits omitting
            // trailing fields, but real Azure always sends all ten, so pyamqp never
            // noticed. Sending product metadata matches Azure's behavior anyway.
            open.Properties = new Fields
            {
                [new Symbol("product")] = "OpenServiceBus",
                [new Symbol("version")] =
                    typeof(ListenerEventHandler).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            };
            return;
        }

        // Same pyamqp strictness for begin (frame[7] = properties) and attach
        // (frame[13] = properties): stamp an empty properties map so the encoded field
        // list always reaches the index pyamqp reads. `??=` keeps any real properties
        // (e.g. the session-accept attach's locked-until) intact.
        if (protocolEvent.Id == EventId.SessionLocalOpen && protocolEvent.Context is Begin begin)
        {
            begin.Properties ??= new Fields();
            return;
        }

        if (protocolEvent.Id == EventId.LinkLocalOpen && protocolEvent.Context is Attach attachFrame)
        {
            attachFrame.Properties ??= new Fields();
            return;
        }

        if (protocolEvent.Id == EventId.SendDelivery && protocolEvent.Context is { } delivery
            && DeliveryType.IsInstanceOfType(delivery))
        {
            var userToken = DeliveryUserTokenProperty.GetValue(delivery);
            if (userToken is ReceiveContext rc && rc.UserToken is Guid lockToken)
            {
                DeliveryTagProperty.SetValue(delivery, lockToken.ToByteArray());
            }
        }
    }
}
