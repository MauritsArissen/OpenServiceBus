using Microsoft.Extensions.Logging.Abstractions;
using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;
using OpenServiceBus.Core.Storage;
using OpenServiceBus.InMemoryStorage.Queues;
using OpenServiceBus.InMemoryStorage.Topics;

namespace OpenServiceBus.SqliteStorage.Tests;

/// <summary>
/// The full restart story for pub/sub topology (issue #54): a topic, its subscriptions and
/// their rules are written to SQLite as they are created, and a fresh set of registries over
/// the reopened database comes back with the same routing behaviour - not a match-all
/// $Default. Each test builds registries, closes the store, reopens it against the same file
/// and runs <see cref="EntityRehydrator"/>, which is exactly what the Host does on boot.
/// </summary>
public class SqliteTopologyRehydrationTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"osb-topology-{Guid.NewGuid():N}.db");

    private static SqliteMessageStore Open(string path) =>
        new(new SqliteStorageOptions { DataSource = path }, TimeProvider.System, NullLogger<SqliteMessageStore>.Instance);

    private static (QueueManager Queues, TopicManager Topics) Registries(SqliteMessageStore store)
    {
        var queues = new QueueManager(store);
        return (queues, new TopicManager(queues, store));
    }

    private static async Task<TopicManager> RehydrateAsync(SqliteMessageStore store)
    {
        var (queues, topics) = Registries(store);
        await new EntityRehydrator(store, queues, topics).RunAsync();
        return topics;
    }

    private static MessageFilterContext Msg(Dictionary<string, object?> props) => new()
    {
        ApplicationProperties = props,
        EnqueuedTimeUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Restart_RestoresSubscriptionSettingsThatTheBackingQueueNeverCarried()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                await topics.CreateSubscriptionAsync(new SubscriptionDescriptor
                {
                    TopicName = "events",
                    Name = "billing",
                    RequiresSession = true,
                    ForwardTo = "audit",
                    AutoDeleteOnIdle = TimeSpan.FromMinutes(45),
                    UserMetadata = "owned by billing",
                    MaxDeliveryCount = 7,
                });
            }

            await using var reopened = Open(path);
            var restored = await (await RehydrateAsync(reopened)).GetSubscriptionAsync("events", "billing");

            restored.ShouldNotBeNull();
            restored.RequiresSession.ShouldBeTrue("RequiresSession has no backing-queue mirror to be recovered from");
            restored.ForwardTo.ShouldBe("audit");
            restored.AutoDeleteOnIdle.ShouldBe(TimeSpan.FromMinutes(45));
            restored.UserMetadata.ShouldBe("owned by billing");
            restored.MaxDeliveryCount.ShouldBe(7);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Restart_RestoresCustomRulesWithParametersAndActions()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
                await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = "events",
                    SubscriptionName = "billing",
                    Name = "eu-only",
                    Filter = new SqlFilter("user.region = @region", new Dictionary<string, object?> { ["@region"] = "eu" }),
                    Action = new SqlRuleAction("SET sys.Label = 'checked'"),
                });
                await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = "events",
                    SubscriptionName = "billing",
                    Name = "by-correlation",
                    Filter = new CorrelationFilter { Subject = "invoice", Properties = new Dictionary<string, object?> { ["tier"] = 2 } },
                });
                await topics.DeleteRuleAsync("events", "billing", TopicManager.DefaultRuleName);
            }

            await using var reopened = Open(path);
            var rules = await (await RehydrateAsync(reopened)).ListRulesAsync("events", "billing");

            rules.Select(r => r.Name).OrderBy(n => n).ShouldBe(["by-correlation", "eu-only"]);
            rules.ShouldNotContain(r => r.Name == TopicManager.DefaultRuleName,
                "a deleted $Default must not be resurrected by the fresh-subscription default");

            var sql = rules.Single(r => r.Name == "eu-only");
            var filter = sql.Filter.ShouldBeOfType<SqlFilter>();
            filter.Expression.ShouldBe("user.region = @region");
            filter.Parameters["@region"].ShouldBe("eu");
            sql.Action.ShouldNotBeNull();
            sql.Action.Expression.ShouldBe("SET sys.Label = 'checked'");

            var correlation = rules.Single(r => r.Name == "by-correlation").Filter.ShouldBeOfType<CorrelationFilter>();
            correlation.Subject.ShouldBe("invoice");
            correlation.Properties["tier"].ShouldBe(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Restart_FanOutHonorsTheRestoredFilter_NonMatchingMessageDoesNotLand()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "eu" });
                await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = "events",
                    SubscriptionName = "eu",
                    Name = "eu-only",
                    Filter = new SqlFilter("user.region = 'eu'"),
                });
                await topics.DeleteRuleAsync("events", "eu", TopicManager.DefaultRuleName);
            }

            await using var reopened = Open(path);
            var topics2 = await RehydrateAsync(reopened);

            topics2.EvaluateSubscribers("events", Msg(new Dictionary<string, object?> { ["region"] = "eu" }))
                .ShouldBe(["events/Subscriptions/eu"]);
            topics2.EvaluateSubscribers("events", Msg(new Dictionary<string, object?> { ["region"] = "us" }))
                .ShouldBeEmpty("the restored SQL filter must still reject non-matching publishes");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Restart_TheWinningRulesActionSurvives()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
                await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = "events",
                    SubscriptionName = "billing",
                    Name = "stamp",
                    Filter = new SqlFilter("user.region = 'eu'"),
                    Action = new SqlRuleAction("SET sys.Label = 'stamped'"),
                });
                await topics.DeleteRuleAsync("events", "billing", TopicManager.DefaultRuleName);
            }

            await using var reopened = Open(path);
            var topics2 = await RehydrateAsync(reopened);

            var matches = topics2.EvaluateSubscriberMatches("events", Msg(new Dictionary<string, object?> { ["region"] = "eu" }));
            matches.Count.ShouldBe(1);
            matches[0].Action.ShouldNotBeNull();
            matches[0].Action!.Expression.ShouldBe("SET sys.Label = 'stamped'");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Restart_ConfigDeclaredSubscriptionWins_PersistedRulesDoNotOverrideIt()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
                await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = "events",
                    SubscriptionName = "billing",
                    Name = "persisted",
                    Filter = new SqlFilter("user.region = 'eu'"),
                });
            }

            // The config bootstrap runs before rehydration and declares the same subscription
            // with a rule set of its own - the declarative shape has to survive intact.
            await using var reopened = Open(path);
            var (queues, topics2) = Registries(reopened);
            await topics2.CreateTopicAsync(new TopicDescriptor { Name = "events" });
            await topics2.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
            await topics2.CreateOrReplaceRuleAsync(new RuleDescriptor
            {
                TopicName = "events",
                SubscriptionName = "billing",
                Name = "from-config",
                Filter = new SqlFilter("user.region = 'us'"),
            });

            await new EntityRehydrator(reopened, queues, topics2).RunAsync();

            var rules = await topics2.ListRulesAsync("events", "billing");
            rules.Select(r => r.Name).ShouldContain("from-config");
            rules.Select(r => r.Name).ShouldNotContain("persisted",
                "config.json is the declarative bootstrap and wins where it conflicts");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Restart_LegacyDatabaseWithoutSubscriptionSnapshots_StillRehydratesFromTheBackingQueueScan()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                // Simulates a database written before subscription snapshots existed: the
                // backing queue and its descriptor are there, the subscription rows are not.
                await store.CreateQueueAsync("events/Subscriptions/legacy");
                await store.SaveQueueDescriptorAsync("events/Subscriptions/legacy",
                    QueueDescriptorJson.Serialize(new QueueDescriptor
                    {
                        Name = "events/Subscriptions/legacy",
                        MaxDeliveryCount = 3,
                        Status = EntityStatus.ReceiveDisabled,
                    }));
            }

            await using var reopened = Open(path);
            var topics = await RehydrateAsync(reopened);

            var restored = await topics.GetSubscriptionAsync("events", "legacy");
            restored.ShouldNotBeNull();
            restored.MaxDeliveryCount.ShouldBe(3, "the backing-queue snapshot is still the fallback source");
            restored.Status.ShouldBe(EntityStatus.ReceiveDisabled);
            (await topics.ListRulesAsync("events", "legacy")).Single().Name.ShouldBe(TopicManager.DefaultRuleName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeletingTheTopic_LeavesNoSnapshotsToRehydrate()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
                await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
                {
                    TopicName = "events",
                    SubscriptionName = "billing",
                    Name = "eu-only",
                    Filter = new SqlFilter("user.region = 'eu'"),
                });

                await topics.DeleteTopicAsync("events");

                store.LoadTopicDescriptors().ShouldBeEmpty();
                store.LoadSubscriptionDescriptors().ShouldBeEmpty();
                store.LoadSubscriptionRules().ShouldBeEmpty();
            }

            await using var reopened = Open(path);
            var topics2 = await RehydrateAsync(reopened);

            (await topics2.GetTopicAsync("events")).ShouldBeNull();
            (await topics2.GetSubscriptionAsync("events", "billing")).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeletingASubscription_LeavesNoOrphanedRuleRows()
    {
        await using var store = Open(":memory:");
        var (_, topics) = Registries(store);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "billing",
            Name = "eu-only",
            Filter = new SqlFilter("user.region = 'eu'"),
        });

        await topics.DeleteSubscriptionAsync("events", "billing");

        store.LoadSubscriptionDescriptors().ShouldBeEmpty();
        store.LoadSubscriptionRules().ShouldBeEmpty();
        store.LoadTopicDescriptors().ContainsKey("events").ShouldBeTrue("the topic itself outlives its subscription");
    }

    [Fact]
    public async Task DeletingARule_RemovesOnlyThatRulesRow()
    {
        await using var store = Open(":memory:");
        var (_, topics) = Registries(store);
        await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
        await topics.CreateOrReplaceRuleAsync(new RuleDescriptor
        {
            TopicName = "events",
            SubscriptionName = "billing",
            Name = "eu-only",
            Filter = new SqlFilter("user.region = 'eu'"),
        });

        (await topics.DeleteRuleAsync("events", "billing", "eu-only")).ShouldBeTrue();

        var remaining = store.LoadSubscriptionRules()["events/Subscriptions/billing"];
        remaining.Count.ShouldBe(1);
        RuleDescriptorJson.Deserialize(remaining[0])!.Name.ShouldBe(TopicManager.DefaultRuleName);
    }

    [Fact]
    public async Task UpdatingASubscription_RewritesItsSnapshot()
    {
        var path = TempDb();
        try
        {
            await using (var store = Open(path))
            {
                var (_, topics) = Registries(store);
                await topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
                var sub = await topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "billing" });
                await topics.UpdateSubscriptionAsync(sub with { UserMetadata = "updated", MaxDeliveryCount = 2 });
            }

            await using var reopened = Open(path);
            var restored = await (await RehydrateAsync(reopened)).GetSubscriptionAsync("events", "billing");

            restored.ShouldNotBeNull();
            restored.UserMetadata.ShouldBe("updated");
            restored.MaxDeliveryCount.ShouldBe(2);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
