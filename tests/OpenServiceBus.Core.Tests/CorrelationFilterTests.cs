using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Tests;

public class CorrelationFilterTests
{
    private static MessageFilterContext Message(
        string? correlationId = null,
        string? subject = null,
        string? sessionId = null,
        Dictionary<string, object?>? props = null) =>
        new()
        {
            CorrelationId = correlationId,
            Subject = subject,
            SessionId = sessionId,
            EnqueuedTimeUtc = DateTimeOffset.UnixEpoch,
            ApplicationProperties = props ?? new Dictionary<string, object?>(),
        };

    private static CorrelationFilter WithProperty(string key, object? value) =>
        new() { Properties = new Dictionary<string, object?> { [key] = value } };

    [Fact]
    public void Matches_ExactPropertyNameAndValue_Matches()
    {
        var filter = WithProperty("region", "eu");

        filter.Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeTrue();
        filter.Matches(Message(props: new() { ["region"] = "us" })).ShouldBeFalse();
        filter.Matches(Message(props: new() { ["other"] = "eu" })).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Priority", "priority")]
    [InlineData("priority", "Priority")]
    [InlineData("PRIORITY", "priority")]
    public void Matches_PropertyNameCasingDiffers_StillMatches(string ruleKey, string messageKey)
    {
        var filter = WithProperty(ruleKey, 5L);

        filter.Matches(Message(props: new() { [messageKey] = 5L })).ShouldBeTrue();
        filter.Matches(Message(props: new() { [messageKey] = 6L })).ShouldBeFalse();
    }

    [Theory]
    [InlineData((int)5)]
    [InlineData((long)5)]
    [InlineData((uint)5)]
    [InlineData((ulong)5)]
    [InlineData((short)5)]
    [InlineData((ushort)5)]
    [InlineData((byte)5)]
    [InlineData((sbyte)5)]
    [InlineData(5.0)]
    [InlineData(5.0f)]
    public void Matches_CrossTypeNumericValue_MatchesWhenNumericallyEqual(object messageValue)
    {
        WithProperty("priority", 5L).Matches(Message(props: new() { ["priority"] = messageValue })).ShouldBeTrue();
        WithProperty("priority", 5).Matches(Message(props: new() { ["priority"] = messageValue })).ShouldBeTrue();
        WithProperty("priority", 5.0).Matches(Message(props: new() { ["priority"] = messageValue })).ShouldBeTrue();
        WithProperty("priority", 6L).Matches(Message(props: new() { ["priority"] = messageValue })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_NumericValuesDiffer_DoesNotMatch()
    {
        WithProperty("priority", 5L).Matches(Message(props: new() { ["priority"] = (uint)6 })).ShouldBeFalse();
        WithProperty("priority", 5.5).Matches(Message(props: new() { ["priority"] = 5L })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_NumberAgainstNumericString_DoesNotMatch()
    {
        WithProperty("priority", 5L).Matches(Message(props: new() { ["priority"] = "5" })).ShouldBeFalse();
        WithProperty("priority", "5").Matches(Message(props: new() { ["priority"] = 5L })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_StringPropertyValueCasingDiffers_DoesNotMatch()
    {
        var filter = WithProperty("region", "eu");

        filter.Matches(Message(props: new() { ["region"] = "EU" })).ShouldBeFalse();
        filter.Matches(Message(props: new() { ["Region"] = "EU" })).ShouldBeFalse();
        filter.Matches(Message(props: new() { ["Region"] = "eu" })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_SystemFieldValueCasingDiffers_DoesNotMatch()
    {
        var filter = new CorrelationFilter { CorrelationId = "Order-1" };

        filter.Matches(Message(correlationId: "Order-1")).ShouldBeTrue();
        filter.Matches(Message(correlationId: "order-1")).ShouldBeFalse();
        filter.Matches(Message(correlationId: null)).ShouldBeFalse();

        var subjectFilter = new CorrelationFilter { Subject = "Urgent" };
        subjectFilter.Matches(Message(subject: "Urgent")).ShouldBeTrue();
        subjectFilter.Matches(Message(subject: "urgent")).ShouldBeFalse();

        var sessionFilter = new CorrelationFilter { SessionId = "S1" };
        sessionFilter.Matches(Message(sessionId: "S1")).ShouldBeTrue();
        sessionFilter.Matches(Message(sessionId: "s1")).ShouldBeFalse();
    }

    [Fact]
    public void Matches_BooleanAndMixedConstraints_AllMustMatch()
    {
        var filter = new CorrelationFilter
        {
            CorrelationId = "corr",
            Properties = new Dictionary<string, object?> { ["Priority"] = 5, ["urgent"] = true },
        };

        filter.Matches(Message(correlationId: "corr", props: new() { ["priority"] = (uint)5, ["Urgent"] = true }))
            .ShouldBeTrue();
        filter.Matches(Message(correlationId: "corr", props: new() { ["priority"] = (uint)5, ["Urgent"] = false }))
            .ShouldBeFalse();
        filter.Matches(Message(correlationId: "other", props: new() { ["priority"] = (uint)5, ["Urgent"] = true }))
            .ShouldBeFalse();
    }

    [Fact]
    public void Matches_MissingProperty_DoesNotMatch()
    {
        WithProperty("priority", 5L).Matches(Message()).ShouldBeFalse();
    }

    [Fact]
    public void Matches_NullExpectedValue_MatchesOnlyNullActual()
    {
        WithProperty("flag", null).Matches(Message(props: new() { ["flag"] = null })).ShouldBeTrue();
        WithProperty("flag", null).Matches(Message(props: new() { ["flag"] = "x" })).ShouldBeFalse();
        WithProperty("flag", "x").Matches(Message(props: new() { ["flag"] = null })).ShouldBeFalse();
    }
}
