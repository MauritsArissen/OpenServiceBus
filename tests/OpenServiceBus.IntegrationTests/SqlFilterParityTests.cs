using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using OpenServiceBus.Core.Entities;

namespace OpenServiceBus.IntegrationTests;

/// <summary>
/// SQL filter parity through the real SDK (issue #26): arithmetic, LIKE...ESCAPE, and
/// the built-in functions route messages correctly, invalid expressions are rejected at
/// rule-creation time with a <see cref="ServiceBusException"/>, and <c>newid()</c> works
/// inside rule actions.
/// </summary>
public class SqlFilterParityTests
{
    [Theory]
    [InlineData("priority + 1 >= 5", 4, 3)]
    [InlineData("total % 2 = 0", 4, 5)]
    [InlineData("-offset < 0", 3, -3)]
    [InlineData("code LIKE 'a!_%' ESCAPE '!'", 0, 0)]
    public async Task Rule_WithParityExpression_RoutesOnlyMatchingMessages(string expression, int matchValue, int missValue)
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "parity" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "parity", Name = "filtered" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("parity", "filtered");
        await ruleManager.DeleteRuleAsync("$Default");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("parity-rule", new SqlRuleFilter(expression)));

        var sender = client.CreateSender("parity");
        var match = new ServiceBusMessage("match") { MessageId = "match-1" };
        match.ApplicationProperties["priority"] = matchValue;
        match.ApplicationProperties["total"] = matchValue;
        match.ApplicationProperties["offset"] = matchValue;
        match.ApplicationProperties["code"] = "a_suffix";
        var miss = new ServiceBusMessage("miss") { MessageId = "miss-1" };
        miss.ApplicationProperties["priority"] = missValue;
        miss.ApplicationProperties["total"] = missValue;
        miss.ApplicationProperties["offset"] = missValue;
        miss.ApplicationProperties["code"] = "axsuffix";

        await sender.SendMessageAsync(match);
        await sender.SendMessageAsync(miss);

        var receiver = client.CreateReceiver("parity", "filtered");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        received.ShouldNotBeNull();
        received.MessageId.ShouldBe("match-1");
        await receiver.CompleteMessageAsync(received);
        (await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2))).ShouldBeNull(
            "the non-matching message must not reach the subscription");
    }

    [Theory]
    [InlineData("priority +")]
    [InlineData("priority + 1")]
    [InlineData("unknownfn(x) = 1")]
    [InlineData("code LIKE 'a%' ESCAPE '!!'")]
    public async Task CreateRule_InvalidExpression_IsRejectedAtCreation(string expression)
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "reject" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "reject", Name = "sub" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("reject", "sub");
        await Should.ThrowAsync<ArgumentException>(
            () => ruleManager.CreateRuleAsync(new CreateRuleOptions("bad", new SqlRuleFilter(expression))));

        var rules = new List<string>();
        await foreach (var rule in ruleManager.GetRulesAsync())
        {
            rules.Add(rule.Name);
        }
        rules.ShouldNotContain("bad", "a rejected rule must not be half-created");
    }

    [Fact]
    public async Task CreateRule_InvalidExpression_ViaAdminClient_ThrowsAtCreation()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        var admin = new ServiceBusAdministrationClient(harness.ConnectionString);
        await admin.CreateTopicAsync("reject-atom");
        await admin.CreateSubscriptionAsync("reject-atom", "sub");

        await Should.ThrowAsync<ArgumentException>(
            () => admin.CreateRuleAsync("reject-atom", "sub",
                new CreateRuleOptions("bad", new SqlRuleFilter("priority + 1"))));
    }

    [Fact]
    public async Task RuleAction_WithNewId_StampsAFreshGuidPerDeliveredCopy()
    {
        await using var harness = await IntegrationHarness.StartAsync();
        await harness.Topics.CreateTopicAsync(new TopicDescriptor { Name = "stamped" });
        await harness.Topics.CreateSubscriptionAsync(new SubscriptionDescriptor { TopicName = "stamped", Name = "all" });
        await using var client = new ServiceBusClient(harness.ConnectionString);

        var ruleManager = client.CreateRuleManager("stamped", "all");
        await ruleManager.DeleteRuleAsync("$Default");
        await ruleManager.CreateRuleAsync(new CreateRuleOptions("stamp", new SqlRuleFilter("1=1"))
        {
            Action = new SqlRuleAction("SET trackingId = newid()"),
        });

        var sender = client.CreateSender("stamped");
        await sender.SendMessageAsync(new ServiceBusMessage("one") { MessageId = "m-1" });
        await sender.SendMessageAsync(new ServiceBusMessage("two") { MessageId = "m-2" });

        var receiver = client.CreateReceiver("stamped", "all");
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        var second = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();

        first.ApplicationProperties.ContainsKey("trackingId").ShouldBeTrue();
        second.ApplicationProperties.ContainsKey("trackingId").ShouldBeTrue();
        first.ApplicationProperties["trackingId"].ShouldBeOfType<Guid>();
        first.ApplicationProperties["trackingId"].ShouldNotBe(second.ApplicationProperties["trackingId"]);
        await receiver.CompleteMessageAsync(first);
        await receiver.CompleteMessageAsync(second);
    }
}
