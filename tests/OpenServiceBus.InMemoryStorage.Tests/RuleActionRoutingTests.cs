using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Routing;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class RuleActionRoutingTests
{
    private sealed class MarkingApplier : IRuleActionApplier
    {
        public List<string> Applied { get; } = [];

        public byte[] Apply(byte[] encodedMessage, SqlRuleAction action)
        {
            Applied.Add(action.Expression);
            return [.. encodedMessage, 0xFF];
        }
    }

    private sealed class ThrowingApplier : IRuleActionApplier
    {
        public byte[] Apply(byte[] encodedMessage, SqlRuleAction action) =>
            throw new InvalidOperationException("boom");
    }

    private static MessageFilterContext Msg(Dictionary<string, object?>? props = null) => new()
    {
        ApplicationProperties = props ?? new Dictionary<string, object?>(),
        EnqueuedTimeUtc = DateTimeOffset.UtcNow,
    };

    private static async Task<(MessageRouter Router, TopicManager Topics, InMemoryMessageStore Store)> FixtureWithTwoSubs(
        IRuleActionApplier applier)
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues);
        var router = new MessageRouter(queues, store, NullLogger<MessageRouter>.Instance, topics, applier);

        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "acted" });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "acted",
            Name = "$Default",
            Filter = TrueFilter.Instance,
            Action = new SqlRuleAction("SET sys.Label = 'tagged'"),
        });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "plain" });
        return (router, topics, store);
    }

    [Fact]
    public async Task RouteAsync_ActionRule_MutatesOnlyThatSubscriptionsCopy()
    {
        var applier = new MarkingApplier();
        var (router, _, store) = await FixtureWithTwoSubs(applier);
        byte[] original = [0x01, 0x02];

        await router.RouteAsync("events", original, filterContext: Msg());

        applier.Applied.ShouldBe(["SET sys.Label = 'tagged'"]);
        var acted = store.Peek("events/Subscriptions/acted", 0, 10).Single();
        acted.EncodedMessage.ShouldBe(new byte[] { 0x01, 0x02, 0xFF });
        var plain = store.Peek("events/Subscriptions/plain", 0, 10).Single();
        plain.EncodedMessage.ShouldBe(original);
    }

    [Fact]
    public async Task RouteAsync_ActionThrowsAtRuntime_OriginalCopyIsStillDelivered()
    {
        var (router, _, store) = await FixtureWithTwoSubs(new ThrowingApplier());
        byte[] original = [0x0A];

        await router.RouteAsync("events", original, filterContext: Msg());

        store.Peek("events/Subscriptions/acted", 0, 10).Single().EncodedMessage.ShouldBe(original);
        store.Peek("events/Subscriptions/plain", 0, 10).Single().EncodedMessage.ShouldBe(original);
    }

    [Fact]
    public async Task EvaluateSubscriberMatches_MultipleMatchingRules_FirstByNameOrderProvidesTheAction()
    {
        var store = new InMemoryMessageStore();
        var topics = new TopicManager(new QueueManager(store));
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "s" });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events", SubscriptionName = "s", Name = "b-rule",
            Filter = TrueFilter.Instance, Action = new SqlRuleAction("SET x = 2"),
        });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events", SubscriptionName = "s", Name = "a-rule",
            Filter = TrueFilter.Instance, Action = new SqlRuleAction("SET x = 1"),
        });
        await topics.DeleteRuleAsync("events", "s", "$Default");

        var matches = topics.EvaluateSubscriberMatches("events", Msg());

        matches.Count.ShouldBe(1);
        matches[0].Action.ShouldNotBeNull();
        matches[0].Action!.Expression.ShouldBe("SET x = 1");
    }
}
