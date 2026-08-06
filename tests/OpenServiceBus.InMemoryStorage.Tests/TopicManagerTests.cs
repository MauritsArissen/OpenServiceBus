using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.InMemoryStorage.Tests;

public class TopicManagerTests
{
    private static (TopicManager Topics, QueueManager Queues, InMemoryMessageStore Store) NewFixture()
    {
        var store = new InMemoryMessageStore();
        var queues = new QueueManager(store);
        var topics = new TopicManager(queues);
        return (topics, queues, store);
    }

    private static MessageFilterContext Msg(string? subject = null, Dictionary<string, object?>? props = null) =>
        new()
        {
            Subject = subject,
            ApplicationProperties = props ?? new Dictionary<string, object?>(),
            EnqueuedTimeUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task CreateSubscriptionAsync_FreshSubscription_AlsoCreatesBackingQueueAndDefaultTrueRule()
    {
        // Arrange
        var (topics, queues, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });

        // Act
        var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
        var backing = await queues.GetAsync(sub.BackingQueueName);
        var rules = await topics.ListRulesAsync("events", "billing");

        // Assert
        backing.ShouldNotBeNull("backing queue must exist so receivers can attach to it");
        rules.Count.ShouldBe(1);
        rules[0].Name.ShouldBe("$Default");
        rules[0].Filter.ShouldBeOfType<TrueFilter>();
    }

    [Fact]
    public async Task CreateSubscriptionAsync_UnknownTopic_ThrowsInvalidOperation()
    {
        // Arrange
        var (topics, _, _) = NewFixture();

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(() =>
            topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "missing", Name = "sub" }));
    }

    [Fact]
    public async Task EvaluateSubscribers_ThreeSubsWithDifferentSqlFilters_OnlyMatchingOnesReturned()
    {
        // Arrange
        var (topics, _, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "eu" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "us" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "everything" });

        // Replace $Default on eu/us with targeted SQL filters; everything keeps $Default (TrueFilter).
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "eu",
            Name = "$Default",
            Filter = new SqlFilter("region = 'eu'"),
        });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "us",
            Name = "$Default",
            Filter = new SqlFilter("region = 'us'"),
        });

        // Act
        var matchedEu = topics.EvaluateSubscribers("events", Msg(props: new() { ["region"] = "eu" }));
        var matchedUs = topics.EvaluateSubscribers("events", Msg(props: new() { ["region"] = "us" }));
        var matchedZa = topics.EvaluateSubscribers("events", Msg(props: new() { ["region"] = "za" }));

        // Assert
        matchedEu.OrderBy(n => n).ShouldBe(new[]
        {
            EntityNames.SubscriptionAddress("events", "eu"),
            EntityNames.SubscriptionAddress("events", "everything"),
        });
        matchedUs.OrderBy(n => n).ShouldBe(new[]
        {
            EntityNames.SubscriptionAddress("events", "everything"),
            EntityNames.SubscriptionAddress("events", "us"),
        });
        matchedZa.ShouldBe(new[] { EntityNames.SubscriptionAddress("events", "everything") },
            "only the catch-all subscription matches a region we have no targeted rule for");
    }

    [Fact]
    public async Task EvaluateSubscribers_SubscriptionWithNoRules_IsExcluded()
    {
        // Arrange
        var (topics, _, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "drained" });
        (await topics.DeleteRuleAsync("events", "drained", "$Default")).ShouldBeTrue();

        // Act
        var matched = topics.EvaluateSubscribers("events", Msg());

        // Assert
        matched.ShouldBeEmpty("a subscription with zero rules matches nothing - Azure SB's behavior");
    }

    [Fact]
    public async Task CreateOrReplaceRuleAsync_SecondCallWithSameName_OverwritesExistingRule()
    {
        // Arrange
        var (topics, _, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "sub" });

        // Act
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "sub",
            Name = "filter-eu",
            Filter = new SqlFilter("region = 'eu'"),
        });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "sub",
            Name = "filter-eu",
            Filter = new SqlFilter("region = 'eu-west'"),
        });
        var rules = await topics.ListRulesAsync("events", "sub");

        // Assert
        var custom = rules.Single(r => r.Name == "filter-eu");
        ((SqlFilter)custom.Filter).Expression.ShouldBe("region = 'eu-west'");
    }

    [Fact]
    public async Task DeleteSubscriptionAsync_TearsDownBackingQueueAndRules()
    {
        // Arrange
        var (topics, queues, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "sub" });

        // Act
        var deleted = await topics.DeleteSubscriptionAsync("events", "sub");

        // Assert
        deleted.ShouldBeTrue();
        (await queues.GetAsync(sub.BackingQueueName)).ShouldBeNull();
        (await topics.ListRulesAsync("events", "sub")).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteTopicAsync_CascadesToAllSubscriptionsAndTheirBackingQueues()
    {
        // Arrange
        var (topics, queues, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "a" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "b" });

        // Act
        var deleted = await topics.DeleteTopicAsync("events");

        // Assert
        deleted.ShouldBeTrue();
        (await topics.ListSubscriptionsAsync("events")).ShouldBeEmpty();
        (await queues.GetAsync(EntityNames.SubscriptionAddress("events", "a"))).ShouldBeNull();
        (await queues.GetAsync(EntityNames.SubscriptionAddress("events", "b"))).ShouldBeNull();
    }

    [Fact]
    public async Task EvaluateSubscribers_FilterThrowsAtEvaluation_CountsAsNonMatchWithoutFailingFanOut()
    {
        // Arrange - one sub whose filter does arithmetic on a string property (throws at
        // evaluation), one healthy sub. The publish must reach the healthy sub only.
        var (topics, _, _) = NewFixture();
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "broken" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "healthy" });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events", SubscriptionName = "broken", Name = "$Default",
            Filter = new SqlFilter("region + 1 > 0"),
        });

        // Act
        var matched = topics.EvaluateSubscribers("events", Msg(props: new() { ["region"] = "eu" }));

        // Assert
        matched.ShouldBe(new[] { EntityNames.SubscriptionAddress("events", "healthy") });
    }
}
