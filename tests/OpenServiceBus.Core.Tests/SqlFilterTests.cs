using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Tests;

public class SqlFilterTests
{
    private static MessageFilterContext Message(
        string? subject = null,
        string? messageId = null,
        string? sessionId = null,
        Dictionary<string, object?>? props = null) =>
        new()
        {
            Subject = subject,
            MessageId = messageId,
            SessionId = sessionId,
            EnqueuedTimeUtc = DateTimeOffset.UnixEpoch,
            ApplicationProperties = props ?? new Dictionary<string, object?>(),
        };

    [Fact]
    public void Matches_BarePropertyEqualsStringLiteral_ResolvesAgainstApplicationProperties()
    {
        // Arrange
        var filter = new SqlFilter("region = 'eu'");

        // Act + Assert
        filter.Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeTrue();
        filter.Matches(Message(props: new() { ["region"] = "us" })).ShouldBeFalse();
        filter.Matches(Message(props: new() { ["other"] = "eu" })).ShouldBeFalse("missing prop → NULL = … → NULL → false");
    }

    [Fact]
    public void Matches_SystemPropertyPrefix_ResolvesAgainstSystemProperties()
    {
        // Arrange
        var filter = new SqlFilter("sys.Subject = 'urgent'");

        // Act + Assert
        filter.Matches(Message(subject: "urgent")).ShouldBeTrue();
        filter.Matches(Message(subject: "normal")).ShouldBeFalse();
        filter.Matches(Message(subject: null)).ShouldBeFalse();
    }

    [Fact]
    public void Matches_UserPrefix_ResolvesAgainstApplicationProperties()
    {
        // Arrange
        var filter = new SqlFilter("user.region = 'eu'");

        // Act + Assert
        filter.Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeTrue();
        filter.Matches(Message(props: new() { ["region"] = "us" })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_AndOr_ShortCircuitsCorrectlyWithThreeValuedLogic()
    {
        var f = new SqlFilter("region = 'eu' AND priority > 5");

        f.Matches(Message(props: new() { ["region"] = "eu", ["priority"] = 7 })).ShouldBeTrue();
        f.Matches(Message(props: new() { ["region"] = "eu", ["priority"] = 3 })).ShouldBeFalse();
        f.Matches(Message(props: new() { ["region"] = "us", ["priority"] = 7 })).ShouldBeFalse();

        var orFilter = new SqlFilter("region = 'eu' OR priority > 5");
        orFilter.Matches(Message(props: new() { ["region"] = "us", ["priority"] = 7 })).ShouldBeTrue();
        orFilter.Matches(Message(props: new() { ["region"] = "us", ["priority"] = 3 })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_NotAndParentheses_GroupCorrectly()
    {
        var f = new SqlFilter("NOT (region = 'eu' OR region = 'apac')");

        f.Matches(Message(props: new() { ["region"] = "us" })).ShouldBeTrue();
        f.Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeFalse();
        f.Matches(Message(props: new() { ["region"] = "apac" })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_LikeWildcard_PercentMatchesAnyRun_UnderscoreMatchesOneChar()
    {
        new SqlFilter("region LIKE 'eu-%'").Matches(Message(props: new() { ["region"] = "eu-west" })).ShouldBeTrue();
        new SqlFilter("region LIKE 'eu-%'").Matches(Message(props: new() { ["region"] = "us-east" })).ShouldBeFalse();
        new SqlFilter("region LIKE 'us_east'").Matches(Message(props: new() { ["region"] = "us-east" })).ShouldBeTrue();
        new SqlFilter("region LIKE 'us_east'").Matches(Message(props: new() { ["region"] = "us--east" })).ShouldBeFalse();
        new SqlFilter("region NOT LIKE 'eu-%'").Matches(Message(props: new() { ["region"] = "us-east" })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_IsNullAndIsNotNull_HandleMissingProperties()
    {
        new SqlFilter("region IS NULL").Matches(Message(props: new() { ["other"] = "x" })).ShouldBeTrue("missing prop = NULL");
        new SqlFilter("region IS NOT NULL").Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeTrue();
        new SqlFilter("region IS NULL").Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_InList_AcceptsMembershipAndRejectsOthers()
    {
        var f = new SqlFilter("region IN ('eu', 'apac', 'us')");

        f.Matches(Message(props: new() { ["region"] = "apac" })).ShouldBeTrue();
        f.Matches(Message(props: new() { ["region"] = "za" })).ShouldBeFalse();

        new SqlFilter("region NOT IN ('eu', 'apac')").Matches(Message(props: new() { ["region"] = "us" })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_Exists_ChecksKeyPresenceNotTruthiness()
    {
        new SqlFilter("EXISTS(region)").Matches(Message(props: new() { ["region"] = null })).ShouldBeTrue("EXISTS = key present, value can be null");
        new SqlFilter("EXISTS(region)").Matches(Message(props: new() { ["other"] = "x" })).ShouldBeFalse();
        new SqlFilter("NOT EXISTS(region)").Matches(Message(props: new() { ["other"] = "x" })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_NumericComparisons_HandleMixedTypes()
    {
        var f = new SqlFilter("priority >= 5");

        f.Matches(Message(props: new() { ["priority"] = 7 })).ShouldBeTrue();
        f.Matches(Message(props: new() { ["priority"] = 5 })).ShouldBeTrue();
        f.Matches(Message(props: new() { ["priority"] = 3 })).ShouldBeFalse();
        f.Matches(Message(props: new() { ["priority"] = 5.5 })).ShouldBeTrue("double vs int compare");
    }

    [Fact]
    public void Matches_BooleanLiterals_TrueAndFalseEvaluateDirectly()
    {
        new SqlFilter("TRUE").Matches(Message()).ShouldBeTrue();
        new SqlFilter("FALSE").Matches(Message()).ShouldBeFalse();
        new SqlFilter("region = 'eu' AND TRUE").Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_NullComparison_IsTreatedAsFalseAtTopLevel()
    {
        // missing property = NULL; NULL = anything = NULL; NULL boolean = false.
        new SqlFilter("region = 'eu'").Matches(Message()).ShouldBeFalse();
    }

    [Fact]
    public void Matches_BracketedIdentifier_AllowsHyphensAndKeywordsAsPropertyNames()
    {
        var f = new SqlFilter("[trace-id] = 'abc'");
        f.Matches(Message(props: new() { ["trace-id"] = "abc" })).ShouldBeTrue();
        f.Matches(Message(props: new() { ["trace-id"] = "xyz" })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_StringEscape_ConsecutiveSingleQuotesEncodeOneQuote()
    {
        var f = new SqlFilter("region = 'it''s eu'");
        f.Matches(Message(props: new() { ["region"] = "it's eu" })).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_InvalidExpression_ThrowsWithPositionInfo()
    {
        Should.Throw<FormatException>(() => new SqlFilter("region = ")).Message
            .ShouldContain("position");
    }

    [Fact]
    public void Matches_RealisticCompositeFilter_BehavesLikeAzureServiceBus()
    {
        // Mirror a real-world rule: high-priority EU orders only.
        var f = new SqlFilter("region IN ('eu', 'eu-west') AND priority >= 5 AND sys.Subject LIKE 'order%'");

        f.Matches(Message(
            subject: "order-created",
            props: new() { ["region"] = "eu", ["priority"] = 7 })).ShouldBeTrue();

        f.Matches(Message(
            subject: "order-created",
            props: new() { ["region"] = "us", ["priority"] = 7 })).ShouldBeFalse();

        f.Matches(Message(
            subject: "invoice-created",
            props: new() { ["region"] = "eu", ["priority"] = 7 })).ShouldBeFalse();

        f.Matches(Message(
            subject: "order-created",
            props: new() { ["region"] = "eu-west", ["priority"] = 3 })).ShouldBeFalse();
    }

    [Theory]
    [InlineData("priority + 1 > 5", 5, true)]
    [InlineData("priority + 1 > 5", 4, false)]
    [InlineData("priority - 1 >= 4", 5, true)]
    [InlineData("priority * 2 = 10", 5, true)]
    [InlineData("priority / 2 = 2.5", 5, true)]
    [InlineData("priority % 2 = 1", 5, true)]
    [InlineData("-priority < 0", 5, true)]
    [InlineData("priority > -1", 5, true)]
    public void Matches_ArithmeticExpressions_Evaluate(string expression, int priority, bool expected)
    {
        var filter = new SqlFilter(expression);

        filter.Matches(Message(props: new() { ["priority"] = priority })).ShouldBe(expected);
    }

    [Fact]
    public void Matches_ArithmeticOnMissingProperty_PropagatesNullToFalse()
    {
        var filter = new SqlFilter("missing + 1 > 0");

        filter.Matches(Message()).ShouldBeFalse();
    }
}
