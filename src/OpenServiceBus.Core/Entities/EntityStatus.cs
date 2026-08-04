namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Operational status of a queue, topic, or subscription - mirrors Service Bus's
/// <c>EntityStatus</c>. Behavior is documented in docs/Entity-Status.md.
/// </summary>
public enum EntityStatus
{
    Active,
    Disabled,
    SendDisabled,
    ReceiveDisabled,
}

public static class EntityStatusExtensions
{
    /// <summary>True when new messages may enter the entity.</summary>
    public static bool AcceptsSends(this EntityStatus status) =>
        status is EntityStatus.Active or EntityStatus.ReceiveDisabled;

    /// <summary>True when consumers may receive from the entity.</summary>
    public static bool AcceptsReceives(this EntityStatus status) =>
        status is EntityStatus.Active or EntityStatus.SendDisabled;

    public static EntityStatus Parse(string? value) =>
        Enum.TryParse<EntityStatus>(value, ignoreCase: true, out var status) ? status : EntityStatus.Active;
}
