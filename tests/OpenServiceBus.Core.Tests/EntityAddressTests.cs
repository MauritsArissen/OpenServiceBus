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

    [Theory]
    [InlineData("MyApp.Contracts/OrderSubmitted", EntitySubResource.Main, "MyApp.Contracts/OrderSubmitted")]
    [InlineData("/sales/orders/eu", EntitySubResource.Main, "sales/orders/eu")]
    [InlineData("sales/orders/$DeadLetterQueue", EntitySubResource.DeadLetterQueue, "sales/orders/$DeadLetterQueue")]
    [InlineData("sales/orders/$Transfer/$DeadLetterQueue", EntitySubResource.TransferDeadLetterQueue, "sales/orders/$Transfer/$DeadLetterQueue")]
    [InlineData("sales/orders/$management", EntitySubResource.Management, "sales/orders")]
    public void TryParse_HierarchicalEntityName_ParsesAsSingleEntity(
        string address, EntitySubResource subResource, string backingQueue)
    {
        EntityAddress.TryParse(address, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(EntityKind.Queue);
        parsed.SubResource.ShouldBe(subResource);
        parsed.BackingQueueName.ShouldBe(backingQueue);
    }

    [Theory]
    [InlineData("MyApp.Contracts/OrderSubmitted/Subscriptions/billing", EntitySubResource.Main)]
    [InlineData("MyApp.Contracts/OrderSubmitted/Subscriptions/billing/$DeadLetterQueue", EntitySubResource.DeadLetterQueue)]
    public void TryParse_SubscriptionOnHierarchicalTopic_SplitsTopicAndSubscription(
        string address, EntitySubResource subResource)
    {
        EntityAddress.TryParse(address, out var parsed).ShouldBeTrue();
        parsed.Kind.ShouldBe(EntityKind.Subscription);
        parsed.Entity.ShouldBe("MyApp.Contracts/OrderSubmitted");
        parsed.Subscription.ShouldBe("billing");
        parsed.SubResource.ShouldBe(subResource);
    }

    [Theory]
    [InlineData("sales//orders")]
    [InlineData("sales/orders/")]
    [InlineData("orders/$Unknown")]
    [InlineData("$orders/eu")]
    [InlineData("events/Subscriptions/billing/nested")]
    [InlineData("events/Subscriptions/$billing")]
    [InlineData("events/$x/Subscriptions/billing")]
    public void TryParse_MalformedPath_ReturnsFalse(string address)
    {
        EntityAddress.TryParse(address, out _).ShouldBeFalse();
    }

    [Fact]
    public void EntityNames_TransferDlqNames_AreAlsoDeadLetterNames()
    {
        EntityNames.IsTransferDeadLetterQueue("q/$Transfer/$DeadLetterQueue").ShouldBeTrue();
        EntityNames.IsDeadLetterQueue("q/$Transfer/$DeadLetterQueue").ShouldBeTrue();
        EntityNames.IsTransferDeadLetterQueue("q/$DeadLetterQueue").ShouldBeFalse();
    }
}
