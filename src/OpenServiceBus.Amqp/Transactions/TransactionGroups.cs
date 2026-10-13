using System.Runtime.CompilerServices;

namespace OpenServiceBus.Amqp.Transactions;

/// <summary>
/// Tracks cross-entity transaction groups the way Azure Service Bus does when a client
/// sets <c>EnableCrossEntityTransactions</c>: the SDK opens every link - including the
/// transaction coordinator, which it attaches FIRST - on one shared AMQP session. A session
/// carrying a coordinator link is a transaction group; the first entity link attached on it
/// pins the group's via entity. From then on sender attaches to any entity are allowed
/// (sends are logically routed via the pinned entity), while receiver attaches are only
/// allowed on the via entity itself - Azure refuses the rest with <c>amqp:not-allowed</c>,
/// which the SDK surfaces as <c>InvalidOperationException</c>.
///
/// Sessions are tracked by reference in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so closed sessions fall out with the session object - no explicit cleanup hook needed.
/// </summary>
public sealed class TransactionGroups
{
    private readonly ConditionalWeakTable<object, Group> _groups = new();

    /// <summary>Marks the session as a transaction group - called on a coordinator attach.</summary>
    public void MarkCoordinatorSession(object? session)
    {
        if (session is null) return;
        _groups.GetOrCreateValue(session);
    }

    /// <summary>
    /// Records an entity link attach on <paramref name="session"/>. On a non-group session
    /// this always allows. On a group session the first attach pins the via entity; later
    /// receiver attaches on a different entity are rejected, sender attaches always pass.
    /// </summary>
    public TransactionGroupDecision RegisterEntityAttach(object? session, string entity, bool isClientReceiver)
    {
        if (session is null || !_groups.TryGetValue(session, out var group))
        {
            return TransactionGroupDecision.Allow(viaEntity: null);
        }

        lock (group)
        {
            if (group.ViaEntity is null)
            {
                group.ViaEntity = entity;
                return TransactionGroupDecision.Allow(entity);
            }

            if (isClientReceiver && !string.Equals(group.ViaEntity, entity, StringComparison.OrdinalIgnoreCase))
            {
                return TransactionGroupDecision.Reject(group.ViaEntity);
            }

            return TransactionGroupDecision.Allow(group.ViaEntity);
        }
    }

    private sealed class Group
    {
        public string? ViaEntity;
    }
}

/// <summary>Outcome of registering an entity attach against a transaction group.</summary>
public readonly struct TransactionGroupDecision
{
    private TransactionGroupDecision(bool allowed, string? viaEntity)
    {
        Allowed = allowed;
        ViaEntity = viaEntity;
    }

    public bool Allowed { get; }

    /// <summary>The group's pinned via entity; null when the session is not a group.</summary>
    public string? ViaEntity { get; }

    public static TransactionGroupDecision Allow(string? viaEntity) => new(true, viaEntity);

    public static TransactionGroupDecision Reject(string viaEntity) => new(false, viaEntity);
}
