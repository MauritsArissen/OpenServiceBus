using System.Text.RegularExpressions;
using OpenServiceBus.Explorer.CannedMessages;

namespace OpenServiceBus.Explorer.Tests;

public class DynamicVariablesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Guid_Default_IsLowercaseGuid()
    {
        var resolved = DynamicVariables.Resolve("{{$guid}}", Now)!;

        Guid.TryParse(resolved, out _).ShouldBeTrue(resolved);
        resolved.ShouldBe(resolved.ToLowerInvariant());
    }

    [Fact]
    public void Guid_Upper_IsUppercase()
    {
        var resolved = DynamicVariables.Resolve("{{$guid upper}}", Now)!;

        Guid.TryParse(resolved, out _).ShouldBeTrue(resolved);
        resolved.ShouldBe(resolved.ToUpperInvariant());
    }

    [Fact]
    public void Guid_EveryOccurrenceResolvesIndependently()
    {
        var resolved = DynamicVariables.Resolve("{{$guid}} and {{$guid}}", Now)!;

        var parts = resolved.Split(" and ");
        parts[0].ShouldNotBe(parts[1]);
    }

    [Fact]
    public void Datetime_Iso8601_FormatsUtcRoundTrip()
    {
        DynamicVariables.Resolve("{{$datetime iso8601}}", Now)
            .ShouldBe("2026-08-23T12:00:00.0000000Z");
    }

    [Fact]
    public void Datetime_Rfc1123_FormatsUtc()
    {
        DynamicVariables.Resolve("{{$datetime rfc1123}}", Now)
            .ShouldBe("Sun, 23 Aug 2026 12:00:00 GMT");
    }

    [Theory]
    [InlineData("-5d", "2026-08-18T12:00:00.0000000Z")]
    [InlineData("3h", "2026-08-23T15:00:00.0000000Z")]
    [InlineData("+30m", "2026-08-23T12:30:00.0000000Z")]
    [InlineData("-45s", "2026-08-23T11:59:15.0000000Z")]
    [InlineData("2w", "2026-09-06T12:00:00.0000000Z")]
    [InlineData("-1M", "2026-07-23T12:00:00.0000000Z")]
    [InlineData("1y", "2027-08-23T12:00:00.0000000Z")]
    public void Datetime_Offsets_ShiftFromNow(string offset, string expected)
    {
        DynamicVariables.Resolve($"{{{{$datetime iso8601 {offset}}}}}", Now).ShouldBe(expected);
    }

    [Fact]
    public void Variables_InsideAJsonBody_AreReplacedInPlace()
    {
        var resolved = DynamicVariables.Resolve("{\"id\": \"{{$guid}}\", \"at\": \"{{$datetime iso8601}}\"}", Now)!;

        resolved.ShouldContain("\"at\": \"2026-08-23T12:00:00.0000000Z\"");
        Regex.IsMatch(resolved, "\"id\": \"[0-9a-f-]{36}\"").ShouldBeTrue(resolved);
    }

    [Theory]
    [InlineData("{{$unknown}}")]
    [InlineData("{{$guid sideways}}")]
    [InlineData("{{$datetime klingon}}")]
    [InlineData("{{$datetime iso8601 -5x}}")]
    [InlineData("{{$datetime iso8601 5d extra}}")]
    public void UnknownOrMalformedVariables_AreLeftVerbatim(string template)
    {
        DynamicVariables.Resolve(template, Now).ShouldBe(template);
    }

    [Fact]
    public void PlainText_PassesThroughUntouched()
    {
        DynamicVariables.Resolve("no variables here {not one}", Now).ShouldBe("no variables here {not one}");
        DynamicVariables.Resolve(null, Now).ShouldBeNull();
        DynamicVariables.Resolve("", Now).ShouldBe("");
    }

    [Fact]
    public void ContainsVariables_DetectsOnlyRealTokens()
    {
        DynamicVariables.ContainsVariables("{{$guid}}").ShouldBeTrue();
        DynamicVariables.ContainsVariables("order-{{$guid upper}}").ShouldBeTrue();
        DynamicVariables.ContainsVariables("plain").ShouldBeFalse();
        DynamicVariables.ContainsVariables("{{notDynamic}}").ShouldBeFalse();
        DynamicVariables.ContainsVariables(null).ShouldBeFalse();
    }
}
