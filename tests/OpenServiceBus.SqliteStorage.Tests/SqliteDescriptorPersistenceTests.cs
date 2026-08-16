using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// Descriptor snapshots survive a store restart, which is what lets the Host's
/// rehydration bring entities back with their real settings (status included) instead of
/// defaults. Uses a shared on-disk file per test to simulate the restart. Subscription and
/// rule snapshots (issue #54) are covered here too - they are the rows that decide who
/// receives what after a restart.
/// </summary>
public class SqliteDescriptorPersistenceTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"osb-desc-{Guid.NewGuid():N}.db");

    private static SqliteMessageStore Open(string path) =>
        new(new SqliteStorageOptions { DataSource = path }, TimeProvider.System, NullLogger<SqliteMessageStore>.Instance);

    [Fact]
    public async Task SaveQueueDescriptor_SurvivesReopeningTheStore()
    {
        var path = TempDb();
        try
        {
            var descriptor = new QueueDescriptor { Name = "frozen", Status = EntityStatus.SendDisabled, MaxDeliveryCount = 3 };
            await using (var store = Open(path))
            {
                await store.CreateQueueAsync("frozen");
                await store.SaveQueueDescriptorAsync("frozen", QueueDescriptorJson.Serialize(descriptor));
            }

            await using var reopened = Open(path);
            var loaded = reopened.LoadQueueDescriptors();
            loaded.ContainsKey("frozen").ShouldBeTrue();
            var restored = QueueDescriptorJson.Deserialize(loaded["frozen"]);
            restored.ShouldNotBeNull();
            restored.Status.ShouldBe(EntityStatus.SendDisabled);
            restored.MaxDeliveryCount.ShouldBe(3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveQueueDescriptor_SecondSaveReplacesTheFirst()
    {
        var path = TempDb();
        try
        {
            await using var store = Open(path);
            await store.CreateQueueAsync("q");
            await store.SaveQueueDescriptorAsync("q", QueueDescriptorJson.Serialize(new QueueDescriptor { Name = "q" }));
            await store.SaveQueueDescriptorAsync("q",
                QueueDescriptorJson.Serialize(new QueueDescriptor { Name = "q", Status = EntityStatus.Disabled }));

            var restored = QueueDescriptorJson.Deserialize(store.LoadQueueDescriptors()["q"]);
            restored!.Status.ShouldBe(EntityStatus.Disabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeleteQueue_AlsoRemovesTheDescriptorSnapshot()
    {
        var path = TempDb();
        try
        {
            await using var store = Open(path);
            await store.CreateQueueAsync("gone");
            await store.SaveQueueDescriptorAsync("gone", QueueDescriptorJson.Serialize(new QueueDescriptor { Name = "gone" }));

            await store.DeleteQueueAsync("gone");

            store.LoadQueueDescriptors().ContainsKey("gone").ShouldBeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSubscriptionDescriptor_SurvivesReopeningTheStore_IncludingSubscriptionOnlySettings()
    {
        var path = TempDb();
        try
        {
            var descriptor = new SubscriptionDescriptor
            {
                TopicName = "events",
                Name = "billing",
                RequiresSession = true,
                ForwardTo = "audit",
                AutoDeleteOnIdle = TimeSpan.FromMinutes(30),
                UserMetadata = "owned by billing",
                MaxDeliveryCount = 4,
                Status = EntityStatus.ReceiveDisabled,
            };
            await using (var store = Open(path))
            {
                await store.SaveSubscriptionDescriptorAsync("events", "billing", SubscriptionDescriptorJson.Serialize(descriptor));
            }

            await using var reopened = Open(path);
            var loaded = reopened.LoadSubscriptionDescriptors();
            loaded.ContainsKey("events/Subscriptions/billing").ShouldBeTrue(
                "subscription snapshots are keyed on the canonical subscription address");

            var restored = SubscriptionDescriptorJson.Deserialize(loaded["events/Subscriptions/billing"]);
            restored.ShouldNotBeNull();
            restored.RequiresSession.ShouldBeTrue();
            restored.ForwardTo.ShouldBe("audit");
            restored.AutoDeleteOnIdle.ShouldBe(TimeSpan.FromMinutes(30));
            restored.UserMetadata.ShouldBe("owned by billing");
            restored.MaxDeliveryCount.ShouldBe(4);
            restored.Status.ShouldBe(EntityStatus.ReceiveDisabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSubscriptionRule_SurvivesReopeningTheStore_WithFilterParametersAndAction()
    {
        var path = TempDb();
        try
        {
            var rule = new RuleDescriptor
            {
                TopicName = "events",
                SubscriptionName = "billing",
                Name = "eu-only",
                Filter = new SqlFilter("user.region = @region", new Dictionary<string, object?> { ["@region"] = "eu" }),
                Action = new SqlRuleAction("SET sys.Label = 'checked'"),
            };
            await using (var store = Open(path))
            {
                await store.SaveSubscriptionRuleAsync("events", "billing", "eu-only", RuleDescriptorJson.Serialize(rule));
            }

            await using var reopened = Open(path);
            var rules = reopened.LoadSubscriptionRules();
            rules["events/Subscriptions/billing"].Count.ShouldBe(1);

            var restored = RuleDescriptorJson.Deserialize(rules["events/Subscriptions/billing"][0]);
            restored.ShouldNotBeNull();
            restored.Name.ShouldBe("eu-only");
            var filter = restored.Filter.ShouldBeOfType<SqlFilter>();
            filter.Expression.ShouldBe("user.region = @region");
            filter.Parameters["@region"].ShouldBe("eu");
            restored.Action.ShouldNotBeNull();
            restored.Action.Expression.ShouldBe("SET sys.Label = 'checked'");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSubscriptionRule_SecondSaveUnderTheSameNameReplacesTheFirst()
    {
        await using var store = Open(":memory:");
        await store.SaveSubscriptionRuleAsync("events", "billing", "r", Snapshot("r", "user.region = 'eu'"));
        await store.SaveSubscriptionRuleAsync("events", "billing", "r", Snapshot("r", "user.region = 'us'"));

        var rules = store.LoadSubscriptionRules()["events/Subscriptions/billing"];
        rules.Count.ShouldBe(1);
        RuleDescriptorJson.Deserialize(rules[0])!.Filter.ShouldBeOfType<SqlFilter>()
            .Expression.ShouldBe("user.region = 'us'");
    }

    [Fact]
    public async Task LoadSubscriptionRules_GroupsByAddressAndOrdersByRuleName()
    {
        await using var store = Open(":memory:");
        await store.SaveSubscriptionRuleAsync("events", "billing", "b-second", Snapshot("b-second", "user.n = 2"));
        await store.SaveSubscriptionRuleAsync("events", "billing", "a-first", Snapshot("a-first", "user.n = 1"));
        await store.SaveSubscriptionRuleAsync("events", "shipping", "only", Snapshot("only", "user.n = 3", "shipping"));

        var rules = store.LoadSubscriptionRules();

        rules["events/Subscriptions/billing"].Count.ShouldBe(2);
        rules["events/Subscriptions/shipping"].Count.ShouldBe(1);
        RuleDescriptorJson.Deserialize(rules["events/Subscriptions/billing"][0])!.Name.ShouldBe("a-first",
            "rule-name order keeps the winning rule's action reproducible across a restart");
    }

    [Fact]
    public async Task DeleteSubscriptionRule_RemovesOnlyThatRule()
    {
        await using var store = Open(":memory:");
        await store.SaveSubscriptionRuleAsync("events", "billing", "keep", Snapshot("keep", "user.n = 1"));
        await store.SaveSubscriptionRuleAsync("events", "billing", "drop", Snapshot("drop", "user.n = 2"));

        await store.DeleteSubscriptionRuleAsync("events", "billing", "drop");

        var rules = store.LoadSubscriptionRules()["events/Subscriptions/billing"];
        rules.Count.ShouldBe(1);
        RuleDescriptorJson.Deserialize(rules[0])!.Name.ShouldBe("keep");
    }

    [Fact]
    public async Task DeleteSubscriptionDescriptor_CascadesItsRules()
    {
        await using var store = Open(":memory:");
        await store.SaveSubscriptionDescriptorAsync("events", "billing",
            SubscriptionDescriptorJson.Serialize(new SubscriptionDescriptor { TopicName = "events", Name = "billing" }));
        await store.SaveSubscriptionDescriptorAsync("events", "shipping",
            SubscriptionDescriptorJson.Serialize(new SubscriptionDescriptor { TopicName = "events", Name = "shipping" }));
        await store.SaveSubscriptionRuleAsync("events", "billing", "r", Snapshot("r", "user.n = 1"));
        await store.SaveSubscriptionRuleAsync("events", "shipping", "r", Snapshot("r", "user.n = 2", "shipping"));

        await store.DeleteSubscriptionDescriptorAsync("events", "billing");

        store.LoadSubscriptionDescriptors().ContainsKey("events/Subscriptions/billing").ShouldBeFalse();
        store.LoadSubscriptionRules().ContainsKey("events/Subscriptions/billing").ShouldBeFalse(
            "a subscription's rules cannot outlive it");
        store.LoadSubscriptionDescriptors().ContainsKey("events/Subscriptions/shipping").ShouldBeTrue();
        store.LoadSubscriptionRules().ContainsKey("events/Subscriptions/shipping").ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteTopicDescriptor_CascadesEverySubscriptionAndRuleUnderIt()
    {
        await using var store = Open(":memory:");
        await store.SaveTopicDescriptorAsync("events", TopicDescriptorJson.Serialize(new TopicDescriptor { Name = "events" }));
        await store.SaveTopicDescriptorAsync("other", TopicDescriptorJson.Serialize(new TopicDescriptor { Name = "other" }));
        await store.SaveSubscriptionDescriptorAsync("events", "billing",
            SubscriptionDescriptorJson.Serialize(new SubscriptionDescriptor { TopicName = "events", Name = "billing" }));
        await store.SaveSubscriptionRuleAsync("events", "billing", "r", Snapshot("r", "user.n = 1"));
        await store.SaveSubscriptionDescriptorAsync("other", "keep",
            SubscriptionDescriptorJson.Serialize(new SubscriptionDescriptor { TopicName = "other", Name = "keep" }));
        await store.SaveSubscriptionRuleAsync("other", "keep", "r", Snapshot("r", "user.n = 2", "keep"));

        await store.DeleteTopicDescriptorAsync("events");

        store.LoadTopicDescriptors().ContainsKey("events").ShouldBeFalse();
        store.LoadSubscriptionDescriptors().ContainsKey("events/Subscriptions/billing").ShouldBeFalse();
        store.LoadSubscriptionRules().ContainsKey("events/Subscriptions/billing").ShouldBeFalse();
        store.LoadSubscriptionDescriptors().ContainsKey("other/Subscriptions/keep").ShouldBeTrue(
            "the cascade must stop at the deleted topic");
        store.LoadSubscriptionRules().ContainsKey("other/Subscriptions/keep").ShouldBeTrue();
    }

    private static string Snapshot(string ruleName, string expression, string subscription = "billing") =>
        RuleDescriptorJson.Serialize(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = subscription,
            Name = ruleName,
            Filter = new SqlFilter(expression),
        });
}
