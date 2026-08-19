using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// Gate: <see cref="ServiceBusRuleManager"/> (the SDK's AMQP-based rule manager)
/// drives add-rule, enumerate-rules, and remove-rule end-to-end over the broker's
/// subscription <c>$management</c> endpoint. After managing rules over the wire,
/// publishing to the topic fans out exactly to the subscriptions whose rule matches.
/// </summary>
public class RuleManagementTests
{
    [Fact]
    public async Task AddRule_AfterCreatingSubscription_OverridesDefaultAndRoutesByPredicate()
    {
        // Arrange - topic + sub created via the in-process registry; rules will be managed over AMQP.
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "eu" });

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ruleManager = client.CreateRuleManager("events", "eu");

        // Act - replace $Default (TrueFilter) with a targeted SQL filter over AMQP.
        await ruleManager.DeleteRuleAsync("$Default");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("region-eu", new SqlRuleFilter("region = 'eu'")));

        var sender = client.CreateSender("events");
        var euMsg = new ServiceBusMessage("eu-payload") { MessageId = "eu-1" };
        euMsg.ApplicationProperties["region"] = "eu";
        var usMsg = new ServiceBusMessage("us-payload") { MessageId = "us-1" };
        usMsg.ApplicationProperties["region"] = "us";

        await sender.SendMessageAsync(euMsg);
        await sender.SendMessageAsync(usMsg);

        var euIds = await DrainAsync(client, "events", "eu");

        // Assert - only eu-1 landed in the subscription's backing queue.
        euIds.ShouldBe(new[] { "eu-1" });
    }

    [Fact]
    public async Task GetRulesAsync_DefaultOnly_ReturnsSingleRule()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "single" });

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ruleManager = client.CreateRuleManager("events", "single");

        // Act
        var rules = new List<RuleProperties>();
        await foreach (var rule in ruleManager.GetRulesAsync()) rules.Add(rule);

        // Assert
        rules.Single().Name.ShouldBe("$Default");
        rules.Single().Filter.ShouldBeOfType<TrueRuleFilter>();
    }

    [Fact]
    public async Task GetRulesAsync_ListsBothCorrelationAndSqlRulesAddedViaAmqp()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "mixed" });

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ruleManager = client.CreateRuleManager("events", "mixed");

        await ruleManager.CreateRuleAsync(new CreateRuleOptions("filter-sql", new SqlRuleFilter("priority > 5")));
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("filter-correlation",
            new CorrelationRuleFilter { Subject = "alert" }));

        // Act
        var enumerated = new List<RuleProperties>();
        await foreach (var rule in ruleManager.GetRulesAsync())
        {
            enumerated.Add(rule);
        }

        // Assert
        enumerated.Select(r => r.Name).OrderBy(n => n).ShouldBe(new[] { "$Default", "filter-correlation", "filter-sql" });
        var sql = enumerated.Single(r => r.Name == "filter-sql");
        ((SqlRuleFilter)sql.Filter).SqlExpression.ShouldBe("priority > 5");
        var corr = enumerated.Single(r => r.Name == "filter-correlation");
        ((CorrelationRuleFilter)corr.Filter).Subject.ShouldBe("alert");
    }

    [Fact]
    public async Task DeleteRuleAsync_RemovesRule_AndSubsequentEnumerateOmitsIt()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "scratch" });

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ruleManager = client.CreateRuleManager("events", "scratch");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("temp", new SqlRuleFilter("a = 1")));

        // Act
        await ruleManager.DeleteRuleAsync("temp");

        var remaining = new List<string>();
        await foreach (var rule in ruleManager.GetRulesAsync()) remaining.Add(rule.Name);

        // Assert
        remaining.ShouldNotContain("temp");
        remaining.ShouldContain("$Default", "removing temp must not disturb other rules");
    }

    [Fact]
    public async Task DeleteRuleAsync_UnknownName_ThrowsServiceBusException()
    {
        // Arrange
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "any" });

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ruleManager = client.CreateRuleManager("events", "any");

        // Act + Assert
        await Should.ThrowAsync<ServiceBusException>(() => ruleManager.DeleteRuleAsync("does-not-exist"));
    }

    [Fact]
    public async Task CreateRuleAsync_CorrelationFilterWithNumericProperty_MatchesAcrossNumericWidthAndNameCasing()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = "priority-five" });

        await using var client = new ServiceBusClient(harness.ConnectionString);
        var ruleManager = client.CreateRuleManager("events", "priority-five");
        await ruleManager.DeleteRuleAsync("$Default");
        var filter = new CorrelationRuleFilter();
        filter.ApplicationProperties["Priority"] = 5L;
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("priority-five", filter));

        var sender = client.CreateSender("events");
        var intMatch = new ServiceBusMessage("int-payload") { MessageId = "int-5" };
        intMatch.ApplicationProperties["priority"] = 5;
        var uintMatch = new ServiceBusMessage("uint-payload") { MessageId = "uint-5" };
        uintMatch.ApplicationProperties["priority"] = (uint)5;
        var doubleMatch = new ServiceBusMessage("double-payload") { MessageId = "double-5" };
        doubleMatch.ApplicationProperties["priority"] = 5.0;
        var noMatch = new ServiceBusMessage("other-payload") { MessageId = "int-6" };
        noMatch.ApplicationProperties["priority"] = 6;

        await sender.SendMessageAsync(intMatch);
        await sender.SendMessageAsync(uintMatch);
        await sender.SendMessageAsync(doubleMatch);
        await sender.SendMessageAsync(noMatch);

        var received = await DrainAsync(client, "events", "priority-five");

        received.OrderBy(id => id).ShouldBe(new[] { "double-5", "int-5", "uint-5" });
    }

    private static async Task<List<string>> DrainAsync(ServiceBusClient client, string topic, string subscription)
    {
        var receiver = client.CreateReceiver(topic, subscription, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });
        var ids = new List<string>();
        while (true)
        {
            var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500));
            if (msg is null) break;
            ids.Add(msg.MessageId);
            await receiver.CompleteMessageAsync(msg);
        }
        return ids;
    }
}
