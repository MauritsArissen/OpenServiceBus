using OpenServiceBus.Amqp.Transactions;

namespace OpenServiceBus.Amqp.Tests;

/// <summary>
/// Cross-entity transaction group tracking: a session carrying a coordinator link pins its
/// via entity on the first entity attach; receivers on another entity are then refused,
/// senders pass (docs/Transactions.md, issue #58).
/// </summary>
public class TransactionGroupsTests
{
    [Fact]
    public void RegisterEntityAttach_SessionWithoutCoordinator_AlwaysAllows()
    {
        var groups = new TransactionGroups();
        var session = new object();

        groups.RegisterEntityAttach(session, "a", isClientReceiver: false).Allowed.ShouldBeTrue();
        groups.RegisterEntityAttach(session, "b", isClientReceiver: true).Allowed.ShouldBeTrue();
        groups.RegisterEntityAttach(session, "b", isClientReceiver: true).ViaEntity.ShouldBeNull();
    }

    [Fact]
    public void RegisterEntityAttach_FirstAttachOnGroupSession_PinsViaEntity()
    {
        var groups = new TransactionGroups();
        var session = new object();
        groups.MarkCoordinatorSession(session);

        var decision = groups.RegisterEntityAttach(session, "queue-b", isClientReceiver: false);

        decision.Allowed.ShouldBeTrue();
        decision.ViaEntity.ShouldBe("queue-b");
    }

    [Fact]
    public void RegisterEntityAttach_ReceiverOnSecondEntity_IsRejectedWithViaEntity()
    {
        var groups = new TransactionGroups();
        var session = new object();
        groups.MarkCoordinatorSession(session);
        groups.RegisterEntityAttach(session, "queue-b", isClientReceiver: false);

        var decision = groups.RegisterEntityAttach(session, "queue-a", isClientReceiver: true);

        decision.Allowed.ShouldBeFalse();
        decision.ViaEntity.ShouldBe("queue-b");
    }

    [Fact]
    public void RegisterEntityAttach_SenderOnSecondEntity_IsAllowed()
    {
        var groups = new TransactionGroups();
        var session = new object();
        groups.MarkCoordinatorSession(session);
        groups.RegisterEntityAttach(session, "queue-a", isClientReceiver: true);

        groups.RegisterEntityAttach(session, "queue-b", isClientReceiver: false).Allowed.ShouldBeTrue();
        groups.RegisterEntityAttach(session, "topic-c", isClientReceiver: false).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void RegisterEntityAttach_ReceiverOnViaEntity_IsAllowedCaseInsensitively()
    {
        var groups = new TransactionGroups();
        var session = new object();
        groups.MarkCoordinatorSession(session);
        groups.RegisterEntityAttach(session, "Queue-A", isClientReceiver: false);

        groups.RegisterEntityAttach(session, "queue-a", isClientReceiver: true).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void RegisterEntityAttach_GroupsAreIndependentPerSession()
    {
        var groups = new TransactionGroups();
        var groupSession = new object();
        var otherSession = new object();
        groups.MarkCoordinatorSession(groupSession);
        groups.MarkCoordinatorSession(otherSession);
        groups.RegisterEntityAttach(groupSession, "queue-b", isClientReceiver: false);

        // The other session has its own (unpinned) group: its first attach pins A freely.
        var decision = groups.RegisterEntityAttach(otherSession, "queue-a", isClientReceiver: true);

        decision.Allowed.ShouldBeTrue();
        decision.ViaEntity.ShouldBe("queue-a");
    }
}
