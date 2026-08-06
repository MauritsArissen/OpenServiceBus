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

    [Theory]
    [InlineData("priority + 1 >= 5", 4, true)]
    [InlineData("priority + 1 >= 5", 3, false)]
    [InlineData("total % 2 = 0", 4, true)]
    [InlineData("total % 2 = 0", 5, false)]
    [InlineData("-offset < 0", 3, true)]
    [InlineData("-offset < 0", -3, false)]
    public void Matches_IssueAcceptanceExpressions_Evaluate(string expression, int value, bool expected)
    {
        var filter = new SqlFilter(expression);
        var props = new Dictionary<string, object?> { ["priority"] = value, ["total"] = value, ["offset"] = value };

        filter.Matches(Message(props: props)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("code LIKE 'a!_b' ESCAPE '!'", "a_b", true)]
    [InlineData("code LIKE 'a!_b' ESCAPE '!'", "axb", false)]
    [InlineData("code LIKE '100!%' ESCAPE '!'", "100%", true)]
    [InlineData("code LIKE '100!%' ESCAPE '!'", "1000", false)]
    [InlineData("code LIKE '!!%' ESCAPE '!'", "!anything", true)]
    [InlineData("code NOT LIKE 'a!_b' ESCAPE '!'", "axb", true)]
    public void Matches_LikeWithEscape_TreatsEscapedWildcardsAsLiterals(string expression, string value, bool expected)
    {
        var filter = new SqlFilter(expression);

        filter.Matches(Message(props: new() { ["code"] = value })).ShouldBe(expected);
    }

    [Theory]
    [InlineData("property(region) = 'eu'")]
    [InlineData("p(region) = 'eu'")]
    [InlineData("PROPERTY('region') = 'eu'")]
    [InlineData("property(user.region) = 'eu'")]
    public void Matches_PropertyFunction_ResolvesLikeABareReference(string expression)
    {
        var filter = new SqlFilter(expression);

        filter.Matches(Message(props: new() { ["region"] = "eu" })).ShouldBeTrue();
        filter.Matches(Message(props: new() { ["region"] = "us" })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_PropertyFunctionWithSysScope_ResolvesSystemProperties()
    {
        var filter = new SqlFilter("property(sys.Subject) = 'urgent'");

        filter.Matches(Message(subject: "urgent")).ShouldBeTrue();
        filter.Matches(Message(subject: "calm")).ShouldBeFalse();
    }

    [Fact]
    public void Matches_UnsignedAmqpNumberTypes_CoerceLikeSignedOnes()
    {
        var arithmetic = new SqlFilter("priority + 1 >= 5");
        var compare = new SqlFilter("priority >= 4");

        arithmetic.Matches(Message(props: new() { ["priority"] = (uint)4 })).ShouldBeTrue(
            "rhea (the JS SDK) sends numbers as AMQP uint");
        arithmetic.Matches(Message(props: new() { ["priority"] = (ulong)4 })).ShouldBeTrue();
        arithmetic.Matches(Message(props: new() { ["priority"] = (ushort)4 })).ShouldBeTrue();
        arithmetic.Matches(Message(props: new() { ["priority"] = 4.0 })).ShouldBeTrue();
        compare.Matches(Message(props: new() { ["priority"] = (uint)4 })).ShouldBeTrue();
        compare.Matches(Message(props: new() { ["priority"] = (uint)3 })).ShouldBeFalse();
    }

    [Theory]
    [InlineData("value > 101.5E5", 10160000.0, true)]
    [InlineData("value > 101.5E5", 10140000.0, false)]
    [InlineData("value = 0.5E-2", 0.005, true)]
    [InlineData("value > 1E3", 1500, true)]
    public void Matches_ScientificNotationLiterals_Evaluate(string expression, double value, bool expected)
    {
        var filter = new SqlFilter(expression);

        filter.Matches(Message(props: new() { ["value"] = value })).ShouldBe(expected);
    }

    [Fact]
    public void Matches_QuotedAndDelimitedIdentifiers_ResolveProperties()
    {
        new SqlFilter("\"order id\" = 7").Matches(Message(props: new() { ["order id"] = 7 })).ShouldBeTrue();
        new SqlFilter("[HR-EmployeeID] = 7").Matches(Message(props: new() { ["HR-EmployeeID"] = 7 })).ShouldBeTrue();
        new SqlFilter("[a]]b] = 1").Matches(Message(props: new() { ["a]b"] = 1 })).ShouldBeTrue();
        new SqlFilter("\"say \"\"hi\"\"\" = 1").Matches(Message(props: new() { ["say \"hi\""] = 1 })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_DashBetweenIdentifiers_IsSubtractionNotAnIdentifier()
    {
        var filter = new SqlFilter("total-1 = 3");

        filter.Matches(Message(props: new() { ["total"] = 4 })).ShouldBeTrue(
            "the Service Bus grammar has no '-' in identifiers; total-1 is arithmetic");
        filter.Matches(Message(props: new() { ["total"] = 5 })).ShouldBeFalse();
    }

    [Theory]
    [InlineData("priority = 7", 7, true)]
    [InlineData("priority = 7", 8, false)]
    [InlineData("priority = 7.0", 7, true)]
    [InlineData("price = 2.5", 2.5, true)]
    public void Matches_NumericEquality_CrossesIntegralAndFractionalTypes(string expression, double value, bool expected)
    {
        var asInt = new Dictionary<string, object?> { ["priority"] = (int)value, ["price"] = value };
        var asDouble = new Dictionary<string, object?> { ["priority"] = value, ["price"] = value };

        new SqlFilter(expression).Matches(Message(props: asInt)).ShouldBe(expected,
            "integer literals must be Int64 and compare across numeric types like C#");
        new SqlFilter(expression).Matches(Message(props: asDouble)).ShouldBe(expected);
    }

    [Fact]
    public void Matches_UnaryPlus_IsNumericIdentity()
    {
        new SqlFilter("+priority = 4").Matches(Message(props: new() { ["priority"] = 4 })).ShouldBeTrue();
        new SqlFilter("-(+priority) = -4").Matches(Message(props: new() { ["priority"] = 4 })).ShouldBeTrue();
    }

    [Fact]
    public void Matches_LikeWithExpressionPattern_ResolvesPerMessage()
    {
        var filter = new SqlFilter("code LIKE prefix + '%'");

        filter.Matches(Message(props: new() { ["code"] = "ord-1", ["prefix"] = "ord" })).ShouldBeTrue();
        filter.Matches(Message(props: new() { ["code"] = "inv-1", ["prefix"] = "ord" })).ShouldBeFalse();
        filter.Matches(Message(props: new() { ["code"] = "ord-1" })).ShouldBeFalse("unknown pattern → unknown → no match");
    }

    [Fact]
    public void Matches_ParameterizedFilter_BindsValuesAtCreation()
    {
        var filter = new SqlFilter("priority >= @threshold AND region = @region", new Dictionary<string, object?>
        {
            ["@threshold"] = 5,
            ["region"] = "eu",
        });

        filter.Matches(Message(props: new() { ["priority"] = 7, ["region"] = "eu" })).ShouldBeTrue();
        filter.Matches(Message(props: new() { ["priority"] = 3, ["region"] = "eu" })).ShouldBeFalse();
        filter.Matches(Message(props: new() { ["priority"] = 7, ["region"] = "us" })).ShouldBeFalse();
    }

    [Fact]
    public void Matches_DateTimeOffsetParameter_ComparesAgainstEnqueuedTime()
    {
        var filter = new SqlFilter("sys.EnqueuedTimeUtc >= @cutoff", new Dictionary<string, object?>
        {
            ["@cutoff"] = DateTimeOffset.UnixEpoch.AddDays(-1),
        });

        filter.Matches(Message()).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_UndefinedParameter_ThrowsAtCreationTime()
    {
        Should.Throw<FormatException>(() => new SqlFilter("priority >= @nope"))
            .Message.ShouldContain("@nope");
    }

    [Fact]
    public void Matches_NewId_ProducesADistinctGuidPerCall()
    {
        var filter = new SqlFilter("newid() <> newid()");

        filter.Matches(Message()).ShouldBeTrue();
    }

    [Theory]
    [InlineData("priority +")]
    [InlineData("priority + 1")]
    [InlineData("newid()")]
    [InlineData("code LIKE 'a%' ESCAPE '!!'")]
    [InlineData("code LIKE 'a!' ESCAPE '!'")]
    [InlineData("unknownfn(region) = 1")]
    [InlineData("newid(1) IS NOT NULL")]
    [InlineData("property() = 1")]
    public void Constructor_InvalidExpressions_ThrowAtCreationTime(string expression)
    {
        var ex = Should.Throw<Exception>(() => new SqlFilter(expression));
        (ex is FormatException or ArgumentException).ShouldBeTrue(
            $"expected a creation-time parse/validation error, got {ex.GetType().Name}");
    }
}
