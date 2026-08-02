using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// SQL rule actions (issue #21) through the real Azure SDK: a rule's action mutates the
/// matched subscription's copy during fan-out, and the action round-trips through both the
/// AMQP rule manager and the ATOM admin client.
/// </summary>
public class RuleActionTests
{
    private static async Task<IntegrationHarness> StartWithTopic(params string[] subscriptions)
    {
        var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "events" });
        foreach (var name in subscriptions)
        {
            await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "events", Name = name });
        }
        return harness;
    }

    [Fact]
    public async Task Action_SetSysLabel_MutatesThatSubscriptionsCopyOnly()
    {
        await using var harness = await StartWithTopic("acted", "plain");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("events", "acted");
        await ruleManager.DeleteRuleAsync("$Default");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("tag", new TrueRuleFilter())
        {
            Action = new SqlRuleAction("SET sys.Label = 'tagged'"),
        });

        await client.CreateSender("events").SendMessageAsync(
            new ServiceBusMessage("body") { MessageId = "m-1", Subject = "original" });

        var actedReceiver = client.CreateReceiver("events", "acted");
        var acted = await actedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        acted.ShouldNotBeNull();
        acted.Subject.ShouldBe("tagged");
        await actedReceiver.CompleteMessageAsync(acted);

        var plainReceiver = client.CreateReceiver("events", "plain");
        var plain = await plainReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        plain.ShouldNotBeNull();
        plain.Subject.ShouldBe("original");
        await plainReceiver.CompleteMessageAsync(plain);
    }

    [Fact]
    public async Task Action_ArithmeticAndRemove_MutateApplicationProperties()
    {
        await using var harness = await StartWithTopic("acted");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("events", "acted");
        await ruleManager.DeleteRuleAsync("$Default");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("bump", new TrueRuleFilter())
        {
            Action = new SqlRuleAction("SET counter = counter + 1; REMOVE debug"),
        });

        var message = new ServiceBusMessage("body") { MessageId = "m-2" };
        message.ApplicationProperties["counter"] = 41;
        message.ApplicationProperties["debug"] = "drop-me";
        await client.CreateSender("events").SendMessageAsync(message);

        var receiver = client.CreateReceiver("events", "acted");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull();
        received.ApplicationProperties["counter"].ShouldBe(42L);
        received.ApplicationProperties.ContainsKey("debug").ShouldBeFalse();
        await receiver.CompleteMessageAsync(received);
    }

    [Fact]
    public async Task Action_RoundTrips_ThroughTheAmqpRuleManager()
    {
        await using var harness = await StartWithTopic("acted");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("events", "acted");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("tag", new SqlRuleFilter("priority > 5"))
        {
            Action = new SqlRuleAction("SET sys.Label = 'high'"),
        });

        var rules = new List<RuleProperties>();
        await foreach (var rule in ruleManager.GetRulesAsync())
        {
            rules.Add(rule);
        }

        var tagged = rules.Single(r => r.Name == "tag");
        tagged.Action.ShouldBeOfType<SqlRuleAction>().SqlExpression.ShouldBe("SET sys.Label = 'high'");
        rules.Single(r => r.Name == "$Default").Action.ShouldBeNull();
    }

    [Fact]
    public async Task Action_RoundTrips_ThroughTheAtomAdminClient()
    {
        await using var harness = await StartWithTopic("acted");
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);

        await admin.CreateRuleAsync("events", "acted", new CreateRuleOptions("tag", new TrueRuleFilter())
        {
            Action = new SqlRuleAction("SET sys.CorrelationId = 'routed'"),
        });

        RuleProperties fetched = await admin.GetRuleAsync("events", "acted", "tag");
        fetched.Action.ShouldBeOfType<SqlRuleAction>().SqlExpression.ShouldBe("SET sys.CorrelationId = 'routed'");

        fetched.Action = new SqlRuleAction("SET sys.CorrelationId = 'rerouted'");
        await admin.UpdateRuleAsync("events", "acted", fetched);
        RuleProperties updated = await admin.GetRuleAsync("events", "acted", "tag");
        updated.Action.ShouldBeOfType<SqlRuleAction>().SqlExpression.ShouldBe("SET sys.CorrelationId = 'rerouted'");
    }

    [Fact]
    public async Task Action_InvalidExpression_IsRejectedAtRuleCreation()
    {
        await using var harness = await StartWithTopic("acted");
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("events", "acted");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => ruleManager.CreateRuleAsync(
            new CreateRuleOptions("bad", new TrueRuleFilter())
            {
                Action = new SqlRuleAction("SET sys.MessageId = 'nope'"),
            }));
        ex.Message.ShouldContain("sys.MessageId");
    }
}
