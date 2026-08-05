using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.Core.Tests;

/// <summary>
/// Address parsing for the transfer dead-letter sub-entity (issue #25). The transfer
/// suffix ends with the plain DLQ suffix, so ordering of the checks matters.
/// </summary>
public class EntityAddressTests
{
    [Theory]
    [InlineData("orders", EntityKind.Queue, EntitySubResource.Main, "orders")]
    [InlineData("orders/$DeadLetterQueue", EntityKind.Queue, EntitySubResource.DeadLetterQueue, "orders/$DeadLetterQueue")]
    [InlineData("orders/$Transfer/$DeadLetterQueue", EntityKind.Queue, EntitySubResource.TransferDeadLetterQueue, "orders/$Transfer/$DeadLetterQueue")]
    [InlineData("events/Subscriptions/billing", EntityKind.Subscription, EntitySubResource.Main, "events/Subscriptions/billing")]
    [InlineData("events/Subscriptions/billing/$Transfer/$DeadLetterQueue", EntityKind.Subscription, EntitySubResource.TransferDeadLetterQueue, "events/Subscriptions/billing/$Transfer/$DeadLetterQueue")]
    public void TryParse_ResolvesKindSubResourceAndBackingQueue(
        string address, EntityKind kind, EntitySubResource subResource, string backingQueue)
    {
        EntityAddress.TryParse(address, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(kind);
        parsed.SubResource.ShouldBe(subResource);
        parsed.BackingQueueName.ShouldBe(backingQueue);
    }

    [Fact]
    public void TryParse_JavaSdkLowercaseTransferDlq_ParsesToTheCanonicalBackingQueue()
    {
        EntityAddress.TryParse("orders/$Transfer/$deadletterqueue", out var parsed).ShouldBeTrue();
        parsed.SubResource.ShouldBe(EntitySubResource.TransferDeadLetterQueue);
        parsed.BackingQueueName.ShouldBe("orders/$Transfer/$DeadLetterQueue");
    }

    [Fact]
    public void TryParse_SdkStyleLeadingSlash_TransferDlq_Parses()
    {
        EntityAddress.TryParse("/orders/$Transfer/$DeadLetterQueue", out var parsed).ShouldBeTrue();
        parsed.SubResource.ShouldBe(EntitySubResource.TransferDeadLetterQueue);
        parsed.Entity.ShouldBe("orders");
    }

    [Fact]
    public void EntityNames_TransferDlqNames_AreAlsoDeadLetterNames()
    {
        EntityNames.IsTransferDeadLetterQueue("q/$Transfer/$DeadLetterQueue").ShouldBeTrue();
        EntityNames.IsDeadLetterQueue("q/$Transfer/$DeadLetterQueue").ShouldBeTrue();
        EntityNames.IsTransferDeadLetterQueue("q/$DeadLetterQueue").ShouldBeFalse();
    }
}
