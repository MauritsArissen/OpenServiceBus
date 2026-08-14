using Amqp.Framing;
using Amqp.Types;
using OpenServiceBus.Amqp.Routing;

namespace OpenServiceBus.Amqp.Settlement;

/// <summary>
/// Thrown by the transactional disposition replay when a lock was lost between enlist and
/// commit - it escapes to <c>CoordinatorProcessor</c>, which fails the discharge, so the
/// client's commit throws instead of committing a settled no-op.
/// </summary>
public sealed class LockLostException(string message) : Exception(message);

/// <summary>
/// The settle outcomes real Service Bus sends when a disposition arrives for a lock it no
/// longer holds. The SDKs map the conditions to
/// <c>ServiceBusFailureReason.MessageLockLost</c> / <c>SessionLockLost</c>. Errors always
/// carry <c>Info</c>: pyamqp indexes the 3-element error list and hangs forever otherwise.
/// </summary>
internal static class LockLostOutcome
{
    public static Rejected Message(string entityName) => new()
    {
        Error = new Error(new Symbol(ServiceBusErrors.MessageLockLost))
        {
            Info = new Fields(),
            Description = "The lock supplied is invalid. Either the lock expired, or the message "
                + $"has already been removed from '{entityName}'.",
        },
    };

    public static Rejected Session(string sessionId) => new()
    {
        Error = new Error(new Symbol(ServiceBusErrors.SessionLockLost))
        {
            Info = new Fields(),
            Description = $"The session lock for session '{sessionId}' was lost. Accept the session again to continue.",
        },
    };
}
