using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Tests;

public class EntityStatusTests
{
    [Theory]
    [InlineData(EntityStatus.Active, true, true)]
    [InlineData(EntityStatus.Disabled, false, false)]
    [InlineData(EntityStatus.SendDisabled, false, true)]
    [InlineData(EntityStatus.ReceiveDisabled, true, false)]
    public void StatusSemantics_SendAndReceiveGates(EntityStatus status, bool sends, bool receives)
    {
        status.AcceptsSends().ShouldBe(sends);
        status.AcceptsReceives().ShouldBe(receives);
    }

    [Theory]
    [InlineData("Disabled", EntityStatus.Disabled)]
    [InlineData("sendDisabled", EntityStatus.SendDisabled)]
    [InlineData("RECEIVEDISABLED", EntityStatus.ReceiveDisabled)]
    [InlineData("Active", EntityStatus.Active)]
    [InlineData(null, EntityStatus.Active)]
    [InlineData("", EntityStatus.Active)]
    [InlineData("garbage", EntityStatus.Active)]
    public void Parse_IsCaseInsensitiveAndDefaultsToActive(string? value, EntityStatus expected)
    {
        EntityStatusExtensions.Parse(value).ShouldBe(expected);
    }

    [Fact]
    public void QueueDescriptorJson_RoundTripsStatusAndSettings()
    {
        var descriptor = new QueueDescriptor
        {
            Name = "frozen",
            Status = EntityStatus.SendDisabled,
            LockDuration = TimeSpan.FromSeconds(42),
            MaxDeliveryCount = 3,
            RequiresSession = true,
            DefaultMessageTimeToLive = TimeSpan.FromMinutes(5),
            ForwardDeadLetteredMessagesTo = "graveyard",
        };

        var restored = QueueDescriptorJson.Deserialize(QueueDescriptorJson.Serialize(descriptor));

        restored.ShouldNotBeNull();
        restored.Status.ShouldBe(EntityStatus.SendDisabled);
        restored.LockDuration.ShouldBe(TimeSpan.FromSeconds(42));
        restored.MaxDeliveryCount.ShouldBe(3);
        restored.RequiresSession.ShouldBeTrue();
        restored.DefaultMessageTimeToLive.ShouldBe(TimeSpan.FromMinutes(5));
        restored.ForwardDeadLetteredMessagesTo.ShouldBe("graveyard");
    }

    [Fact]
    public void QueueDescriptorJson_MalformedInput_ReturnsNull()
    {
        QueueDescriptorJson.Deserialize("{not json").ShouldBeNull();
    }
}
