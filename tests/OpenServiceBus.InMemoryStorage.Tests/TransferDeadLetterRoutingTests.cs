using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Routing;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

/// <summary>
/// Transfer dead-letter routing (issue #25): forward hops that cannot be delivered land
/// in the forwarding entity's <c>$Transfer/$DeadLetterQueue</c> with a descriptive reason
/// instead of being dropped.
/// </summary>
public class TransferDeadLetterRoutingTests
{
    private sealed class StubAnnotator : IDeadLetterAnnotator
    {
        public readonly List<(string Source, string Reason, string Description)> Calls = new();

        public byte[] Annotate(byte[] encodedMessage, string sourceEntity, string reason, string description)
        {
            Calls.Add((sourceEntity, reason, description));
            return encodedMessage;
        }
    }

    private static (MessageRouter Router, QueueManager Queues, TopicManager Topics, InMemoryMessageStore Store, StubAnnotator Annotator) NewFixture()
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues);
        var annotator = new StubAnnotator();
        var router = new MessageRouter(queues, store, NullLogger<MessageRouter>.Instance, topics,
            deadLetterAnnotator: annotator);
        return (router, queues, topics, store, annotator);
    }

    [Fact]
    public async Task CreateQueue_AlsoCreatesTheTransferDeadLetterSibling()
    {
        var (_, queues, _, store, _) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "orders" });

        (await queues.GetAsync("orders/$Transfer/$DeadLetterQueue")).ShouldNotBeNull();
        (await store.CountAsync("orders/$Transfer/$DeadLetterQueue")).ShouldBe(0L);

        await queues.DeleteAsync("orders");
        (await queues.GetAsync("orders/$Transfer/$DeadLetterQueue")).ShouldBeNull();
    }

    [Fact]
    public async Task Forward_ToDeletedTarget_LandsInTheSourcesTransferDlq()
    {
        var (router, queues, _, store, annotator) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "a", ForwardTo = "b" });

        var landed = await router.RouteAsync("b", new byte[] { 1 }, forwardSource: "a");

        landed.ShouldBe(new[] { "a/$Transfer/$DeadLetterQueue" });
        (await store.CountAsync("a/$Transfer/$DeadLetterQueue")).ShouldBe(1L);
        annotator.Calls.ShouldHaveSingleItem();
        annotator.Calls[0].Source.ShouldBe("a");
        annotator.Calls[0].Reason.ShouldBe("MessagingEntityNotFound");
    }

    [Fact]
    public async Task Forward_ToDisabledTarget_LandsInTheSourcesTransferDlq()
    {
        var (router, queues, _, store, annotator) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "a", ForwardTo = "b" });
        await queues.CreateAsync(new QueueDescriptor { Name = "b", Status = EntityStatus.SendDisabled });

        await router.RouteAsync("b", new byte[] { 1 }, forwardSource: "a");

        (await store.CountAsync("a/$Transfer/$DeadLetterQueue")).ShouldBe(1L);
        (await store.CountAsync("b")).ShouldBe(0L);
        annotator.Calls[0].Reason.ShouldBe("MessagingEntityDisabled");
    }

    [Fact]
    public async Task Forward_ToFullTarget_LandsInTheSourcesTransferDlq()
    {
        var (router, queues, _, store, annotator) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "a", ForwardTo = "b" });
        await queues.CreateAsync(new QueueDescriptor { Name = "b", MaxSizeInMegabytes = 1 });
        await store.EnqueueAsync("b", new byte[1024 * 1024]);

        await router.RouteAsync("b", new byte[] { 1 }, forwardSource: "a");

        (await store.CountAsync("a/$Transfer/$DeadLetterQueue")).ShouldBe(1L);
        (await store.CountAsync("b")).ShouldBe(1L);
        annotator.Calls[0].Reason.ShouldBe("QuotaExceeded");
    }

    [Fact]
    public async Task ForwardCycle_ExceedingTheHopCap_LandsInTheTransferDlqWhereItTripped()
    {
        var (router, queues, _, store, annotator) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "ring-a", ForwardTo = "ring-b" });
        await queues.CreateAsync(new QueueDescriptor { Name = "ring-b", ForwardTo = "ring-a" });

        var landed = await router.RouteAsync("ring-b", new byte[] { 1 }, forwardSource: "ring-a");

        landed.ShouldHaveSingleItem();
        landed[0].ShouldEndWith("/$Transfer/$DeadLetterQueue");
        annotator.Calls.ShouldHaveSingleItem();
        annotator.Calls[0].Reason.ShouldBe("MaxTransferHopCountExceeded");
        (await store.CountAsync(landed[0])).ShouldBe(1L);
        (await store.CountAsync("ring-a")).ShouldBe(0L);
        (await store.CountAsync("ring-b")).ShouldBe(0L);
    }

    [Fact]
    public async Task Forward_ToDisabledTopic_LandsInTheSourcesTransferDlq()
    {
        var (router, queues, topics, store, annotator) = NewFixture();
        await queues.CreateAsync(new QueueDescriptor { Name = "a", ForwardTo = "t" });
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "t", Status = EntityStatus.Disabled });

        await router.RouteAsync("t", new byte[] { 1 }, forwardSource: "a");

        (await store.CountAsync("a/$Transfer/$DeadLetterQueue")).ShouldBe(1L);
        annotator.Calls[0].Reason.ShouldBe("MessagingEntityDisabled");
    }

    [Fact]
    public async Task DirectRoute_ToMissingTarget_IsStillDroppedWithoutAForwardSource()
    {
        var (router, _, _, _, annotator) = NewFixture();

        var landed = await router.RouteAsync("nowhere", new byte[] { 1 });

        landed.ShouldBeEmpty();
        annotator.Calls.ShouldBeEmpty();
    }
}
