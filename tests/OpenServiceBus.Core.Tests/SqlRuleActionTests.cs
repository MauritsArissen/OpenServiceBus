using OpenServiceBus.Core.Filters;

namespace OpenServiceBus.Core.Tests;

public class SqlRuleActionTests
{
    private sealed class FakeTarget : ISqlRuleActionTarget
    {
        public Dictionary<string, string?> SystemProperties { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object?> ApplicationProperties { get; } = new(StringComparer.Ordinal);

        public MessageFilterContext BuildFilterContext() => new()
        {
            Subject = SystemProperties.GetValueOrDefault("Subject"),
            CorrelationId = SystemProperties.GetValueOrDefault("CorrelationId"),
            EnqueuedTimeUtc = DateTimeOffset.UtcNow,
            ApplicationProperties = new Dictionary<string, object?>(ApplicationProperties, StringComparer.Ordinal),
        };

        public void SetSystemProperty(string canonicalName, string? value) => SystemProperties[canonicalName] = value;
        public void SetApplicationProperty(string name, object? value) => ApplicationProperties[name] = value;
        public void RemoveApplicationProperty(string name) => ApplicationProperties.Remove(name);
    }

    [Fact]
    public void Apply_SetSysLabel_WritesTheCanonicalSubjectProperty()
    {
        var target = new FakeTarget();

        new SqlRuleAction("SET sys.Label = 'tagged'").Apply(target);

        target.SystemProperties["Subject"].ShouldBe("tagged");
    }

    [Fact]
    public void Apply_ArithmeticOnApplicationProperty_UsesTheCurrentValue()
    {
        var target = new FakeTarget();
        target.ApplicationProperties["counter"] = 41;

        new SqlRuleAction("SET counter = counter + 1").Apply(target);

        target.ApplicationProperties["counter"].ShouldBe(42L);
    }

    [Fact]
    public void Apply_SetToNewId_WritesAFreshGuidPerApplication()
    {
        var first = new FakeTarget();
        var second = new FakeTarget();
        var action = new SqlRuleAction("SET trackingId = newid()");

        action.Apply(first);
        action.Apply(second);

        first.ApplicationProperties["trackingId"].ShouldBeOfType<Guid>();
        second.ApplicationProperties["trackingId"].ShouldBeOfType<Guid>();
        first.ApplicationProperties["trackingId"].ShouldNotBe(second.ApplicationProperties["trackingId"]);
    }

    [Fact]
    public void Apply_SetToPropertyFunction_CopiesTheReferencedProperty()
    {
        var target = new FakeTarget();
        target.ApplicationProperties["region"] = "eu";

        new SqlRuleAction("SET zone = property(region)").Apply(target);

        target.ApplicationProperties["zone"].ShouldBe("eu");
    }

    [Fact]
    public void Apply_StatementsRunSequentially_LaterOnesSeeEarlierResults()
    {
        var target = new FakeTarget();

        new SqlRuleAction("SET a = 10; SET b = a * 2 + 1; REMOVE a").Apply(target);

        target.ApplicationProperties.ContainsKey("a").ShouldBeFalse();
        target.ApplicationProperties["b"].ShouldBe(21L);
    }

    [Fact]
    public void Apply_StringConcatenation_WorksWithPlus()
    {
        var target = new FakeTarget();
        target.ApplicationProperties["region"] = "eu";

        new SqlRuleAction("SET sys.CorrelationId = 'route-' + region").Apply(target);

        target.SystemProperties["CorrelationId"].ShouldBe("route-eu");
    }

    [Fact]
    public void Apply_RemoveMissingProperty_IsANoOp()
    {
        var target = new FakeTarget();

        new SqlRuleAction("REMOVE ghost").Apply(target);

        target.ApplicationProperties.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SET sys.MessageId = 'x'")]
    [InlineData("SET sys.SessionId = 'x'")]
    [InlineData("SET sys.EnqueuedTimeUtc = 'x'")]
    [InlineData("REMOVE sys.Label")]
    [InlineData("DROP everything")]
    [InlineData("SET a")]
    [InlineData("SET = 5")]
    [InlineData("")]
    [InlineData(";")]
    public void Constructor_InvalidActions_AreRejectedAtCreationTime(string expression)
    {
        Should.Throw<Exception>(() => new SqlRuleAction(expression))
            .ShouldBeAssignableTo<Exception>();
    }

    [Fact]
    public void Apply_SetToNull_ClearsTheSystemProperty()
    {
        var target = new FakeTarget();
        target.SystemProperties["Subject"] = "old";

        new SqlRuleAction("SET sys.Label = NULL").Apply(target);

        target.SystemProperties["Subject"].ShouldBeNull();
    }
}
