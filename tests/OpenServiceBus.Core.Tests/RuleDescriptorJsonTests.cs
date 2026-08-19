using OpenServiceBus.Core.Entities;
using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Tests;

/// <summary>
/// Rule snapshot round-trips (issue #54). Persisted rules are what stop a restart from
/// silently widening every subscription back to a match-all $Default, so every filter shape
/// the broker accepts - including typed SQL parameters and correlation properties - has to
/// come back out of the snapshot as the same thing that went in.
/// </summary>
public class RuleDescriptorJsonTests
{
    private static RuleDescriptor Rule(RuleFilter filter, SqlRuleAction? action = null) => new()
    {
        TopicName = "events",
        SubscriptionName = "billing",
        Name = "rule-1",
        Filter = filter,
        Action = action,
    };

    private static RuleDescriptor RoundTrip(RuleDescriptor rule)
    {
        var restored = RuleDescriptorJson.Deserialize(RuleDescriptorJson.Serialize(rule));
        restored.ShouldNotBeNull();
        restored.TopicName.ShouldBe(rule.TopicName);
        restored.SubscriptionName.ShouldBe(rule.SubscriptionName);
        restored.Name.ShouldBe(rule.Name);
        return restored;
    }

    private static MessageFilterContext Msg(string? subject = null, Dictionary<string, object?>? props = null) => new()
    {
        Subject = subject,
        ApplicationProperties = props ?? new Dictionary<string, object?>(),
        EnqueuedTimeUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void TrueFilter_RoundTrips()
    {
        RoundTrip(Rule(TrueFilter.Instance)).Filter.ShouldBeOfType<TrueFilter>();
    }

    [Fact]
    public void FalseFilter_RoundTrips()
    {
        RoundTrip(Rule(FalseFilter.Instance)).Filter.ShouldBeOfType<FalseFilter>();
    }

    [Fact]
    public void SqlFilter_RoundTripsTheExpressionAndStillMatchesTheSameMessages()
    {
        var restored = RoundTrip(Rule(new SqlFilter("user.region = 'eu' AND user.tier > 2")));

        var filter = restored.Filter.ShouldBeOfType<SqlFilter>();
        filter.Expression.ShouldBe("user.region = 'eu' AND user.tier > 2");
        filter.Matches(Msg(props: new Dictionary<string, object?> { ["region"] = "eu", ["tier"] = 3 })).ShouldBeTrue();
        filter.Matches(Msg(props: new Dictionary<string, object?> { ["region"] = "us", ["tier"] = 3 })).ShouldBeFalse();
    }

    [Fact]
    public void SqlFilter_ParametersKeepTheirClrTypes()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["@region"] = "eu",
            ["@tier"] = 3,
            ["@big"] = 9_000_000_000L,
            ["@ratio"] = 1.5d,
            ["@flag"] = true,
            ["@when"] = new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.Zero),
            ["@nothing"] = null,
        };

        var restored = RoundTrip(Rule(new SqlFilter("user.region = @region", parameters)));

        var filter = restored.Filter.ShouldBeOfType<SqlFilter>();
        filter.Parameters["@region"].ShouldBe("eu");
        filter.Parameters["@tier"].ShouldBe(3);
        filter.Parameters["@big"].ShouldBe(9_000_000_000L);
        filter.Parameters["@ratio"].ShouldBe(1.5d);
        filter.Parameters["@flag"].ShouldBe(true);
        filter.Parameters["@when"].ShouldBe(new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.Zero));
        filter.Parameters["@nothing"].ShouldBeNull();
    }

    [Fact]
    public void SqlFilter_ParameterisedExpression_StillEvaluatesAfterTheRoundTrip()
    {
        var parameters = new Dictionary<string, object?> { ["@region"] = "eu" };
        var restored = RoundTrip(Rule(new SqlFilter("user.region = @region", parameters)));

        restored.Filter.Matches(Msg(props: new Dictionary<string, object?> { ["region"] = "eu" })).ShouldBeTrue();
        restored.Filter.Matches(Msg(props: new Dictionary<string, object?> { ["region"] = "us" })).ShouldBeFalse();
    }

    [Fact]
    public void CorrelationFilter_RoundTripsEveryFieldAndItsTypedProperties()
    {
        var filter = new CorrelationFilter
        {
            MessageId = "m-1",
            CorrelationId = "c-1",
            Subject = "orders",
            To = "warehouse",
            ReplyTo = "back",
            ReplyToSessionId = "rs-1",
            SessionId = "s-1",
            ContentType = "application/json",
            Properties = new Dictionary<string, object?> { ["priority"] = 5, ["urgent"] = true, ["team"] = "eu" },
        };

        var restored = RoundTrip(Rule(filter)).Filter.ShouldBeOfType<CorrelationFilter>();

        restored.MessageId.ShouldBe("m-1");
        restored.CorrelationId.ShouldBe("c-1");
        restored.Subject.ShouldBe("orders");
        restored.To.ShouldBe("warehouse");
        restored.ReplyTo.ShouldBe("back");
        restored.ReplyToSessionId.ShouldBe("rs-1");
        restored.SessionId.ShouldBe("s-1");
        restored.ContentType.ShouldBe("application/json");
        restored.Properties["priority"].ShouldBe(5);
        restored.Properties["urgent"].ShouldBe(true);
        restored.Properties["team"].ShouldBe("eu");
    }

    [Fact]
    public void CorrelationFilter_WithoutProperties_RoundTripsToAnEmptySet()
    {
        var restored = RoundTrip(Rule(new CorrelationFilter { Subject = "orders" }))
            .Filter.ShouldBeOfType<CorrelationFilter>();

        restored.Properties.ShouldBeEmpty();
        restored.Matches(Msg("orders")).ShouldBeTrue();
        restored.Matches(Msg("other")).ShouldBeFalse();
    }

    [Fact]
    public void Action_RoundTripsItsExpressionAndParameters()
    {
        var action = new SqlRuleAction("SET sys.Label = @label", new Dictionary<string, object?> { ["@label"] = "processed" });

        var restored = RoundTrip(Rule(TrueFilter.Instance, action));

        restored.Action.ShouldNotBeNull();
        restored.Action.Expression.ShouldBe("SET sys.Label = @label");
        restored.Action.Parameters["@label"].ShouldBe("processed");
    }

    [Fact]
    public void NoAction_RoundTripsAsNull()
    {
        RoundTrip(Rule(TrueFilter.Instance)).Action.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        RuleDescriptorJson.Deserialize("{ not json").ShouldBeNull();
    }

    [Fact]
    public void Deserialize_UnparseableFilterExpression_ReturnsNullInsteadOfThrowing()
    {
        var corrupt = """
            {"TopicName":"events","SubscriptionName":"billing","Name":"r",
             "Filter":{"Type":"sql","Expression":"user.region ="}}
            """;

        RuleDescriptorJson.Deserialize(corrupt).ShouldBeNull();
    }

    [Fact]
    public void Deserialize_SnapshotMissingItsIdentity_ReturnsNull()
    {
        RuleDescriptorJson.Deserialize("""{"Filter":{"Type":"true"}}""").ShouldBeNull();
    }

    [Fact]
    public void Deserialize_UnknownFilterShape_ReturnsNullRatherThanWideningToMatchAll()
    {
        var future = """
            {"TopicName":"events","SubscriptionName":"billing","Name":"r",
             "Filter":{"Type":"something-this-version-cannot-read"}}
            """;

        RuleDescriptorJson.Deserialize(future).ShouldBeNull(
            "falling back to a TrueFilter would silently route everything to the subscription");
    }
}
