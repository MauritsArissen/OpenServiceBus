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
    public void Ulid_Is26CrockfordChars_AndTimeOrdered()
    {
        var earlier = DynamicVariables.Resolve("{{$ulid}}", Now)!;
        var later = DynamicVariables.Resolve("{{$ulid}}", Now.AddMinutes(5))!;

        earlier.Length.ShouldBe(26);
        earlier.ShouldAllBe(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c));
        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0, "a later timestamp must sort after an earlier one");
    }

    [Fact]
    public void Sequence_IncrementsPerResolution_ScopedByTemplateAndStart()
    {
        DynamicVariables.ResetSequences();

        DynamicVariables.Resolve("a-{{$sequence}}", Now, sequenceScope: "q1").ShouldBe("a-1");
        DynamicVariables.Resolve("a-{{$sequence}}", Now, sequenceScope: "q1").ShouldBe("a-2");
        DynamicVariables.Resolve("b-{{$sequence}}", Now, sequenceScope: "q1").ShouldBe("b-1");
        DynamicVariables.Resolve("a-{{$sequence}}", Now, sequenceScope: "q2").ShouldBe("a-1");
        DynamicVariables.Resolve("{{$sequence 100}}", Now, sequenceScope: "q1").ShouldBe("100");
        DynamicVariables.Resolve("{{$sequence 100}}", Now, sequenceScope: "q1").ShouldBe("101");

        DynamicVariables.ResetSequences();
        DynamicVariables.Resolve("a-{{$sequence}}", Now, sequenceScope: "q1").ShouldBe("a-1");
    }

    [Fact]
    public void Index_IsTheCopyIndex()
    {
        DynamicVariables.Resolve("copy {{$index}}", Now, copyIndex: 0).ShouldBe("copy 0");
        DynamicVariables.Resolve("copy {{$index}}", Now, copyIndex: 4).ShouldBe("copy 4");
    }

    [Fact]
    public void Datetime_UnixFormats_AndTimestampShorthand()
    {
        DynamicVariables.Resolve("{{$datetime unix}}", Now).ShouldBe(Now.ToUnixTimeSeconds().ToString());
        DynamicVariables.Resolve("{{$datetime unixms}}", Now).ShouldBe(Now.ToUnixTimeMilliseconds().ToString());
        DynamicVariables.Resolve("{{$timestamp}}", Now).ShouldBe(Now.ToUnixTimeSeconds().ToString());
    }

    [Fact]
    public void Datetime_CustomDotNetFormat_IncludingQuotedWithSpaces()
    {
        DynamicVariables.Resolve("{{$datetime 'yyyy-MM-dd'}}", Now).ShouldBe("2026-08-23");
        DynamicVariables.Resolve("{{$datetime 'yyyy-MM-dd HH:mm'}}", Now).ShouldBe("2026-08-23 12:00");
        DynamicVariables.Resolve("{{$datetime 'yyyy-MM-dd' -5d}}", Now).ShouldBe("2026-08-18");
    }

    [Fact]
    public void RandomInt_DefaultAndBoundedRanges()
    {
        for (var i = 0; i < 50; i++)
        {
            var defaulted = long.Parse(DynamicVariables.Resolve("{{$randomInt}}", Now)!);
            defaulted.ShouldBeInRange(0, 1000);
            var bounded = long.Parse(DynamicVariables.Resolve("{{$randomInt 5 7}}", Now)!);
            bounded.ShouldBeInRange(5, 7);
        }
        DynamicVariables.Resolve("{{$randomInt 9 3}}", Now).ShouldBe("{{$randomInt 9 3}}", "min above max is malformed");
        DynamicVariables.Resolve("{{$randomInt 1}}", Now).ShouldBe("{{$randomInt 1}}", "one argument is malformed");
    }

    [Fact]
    public void RandomDouble_RespectsRangeAndDecimals()
    {
        for (var i = 0; i < 25; i++)
        {
            var value = DynamicVariables.Resolve("{{$randomDouble 1.5 2.5 3}}", Now)!;
            double.Parse(value, System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(1.5, 2.5);
            value.Split('.')[1].Length.ShouldBe(3);
        }
        DynamicVariables.Resolve("{{$randomDouble 5 1}}", Now).ShouldBe("{{$randomDouble 5 1}}");
    }

    [Fact]
    public void RandomBoolean_ProducesBothLiterals()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100 && seen.Count < 2; i++)
        {
            seen.Add(DynamicVariables.Resolve("{{$randomBoolean}}", Now)!);
        }
        seen.ShouldBe(["false", "true"], ignoreOrder: true);
    }

    [Fact]
    public void RandomStrings_HonorLengthAndAlphabet()
    {
        var alnum = DynamicVariables.Resolve("{{$randomAlphaNumeric 32}}", Now)!;
        alnum.Length.ShouldBe(32);
        alnum.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c));

        var hex = DynamicVariables.Resolve("{{$randomHex 16}}", Now)!;
        hex.Length.ShouldBe(16);
        hex.ShouldAllBe(c => "0123456789abcdef".Contains(c));

        DynamicVariables.Resolve("{{$randomHex 0}}", Now).ShouldBe("{{$randomHex 0}}");
    }

    [Fact]
    public void RandomChoice_PicksOnlyListedValues()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            seen.Add(DynamicVariables.Resolve("{{$randomChoice eu|us|apac}}", Now)!);
        }
        seen.ShouldBeSubsetOf(["eu", "us", "apac"]);
        seen.Count.ShouldBeGreaterThan(1);

        DynamicVariables.Resolve("{{$randomChoice solo}}", Now).ShouldBe("{{$randomChoice solo}}", "a single option is malformed");
    }

    [Fact]
    public void RandomBase64_DecodesToExactlyTheRequestedBytes()
    {
        var encoded = DynamicVariables.Resolve("{{$randomBase64 1024}}", Now)!;
        Convert.FromBase64String(encoded).Length.ShouldBe(1024);
        DynamicVariables.Resolve("{{$randomBase64 0}}", Now).ShouldBe("{{$randomBase64 0}}");
    }

    [Fact]
    public void Repeat_PadsDeterministically_AndCapsOutput()
    {
        DynamicVariables.Resolve("{{$repeat 'ab' 3}}", Now).ShouldBe("ababab");
        DynamicVariables.Resolve("{{$repeat 'x y' 2}}", Now).ShouldBe("x yx y", "quoted text keeps its spaces");
        DynamicVariables.Resolve("{{$repeat 'x' 2000000}}", Now)
            .ShouldBe("{{$repeat 'x' 2000000}}", "outputs beyond the cap stay verbatim");
    }

    [Fact]
    public void VariableNames_AreCaseInsensitive()
    {
        long.Parse(DynamicVariables.Resolve("{{$RANDOMINT}}", Now)!).ShouldBeInRange(0, 1000);
    }

    [Fact]
    public void UnbalancedQuotes_StayVerbatim()
    {
        DynamicVariables.Resolve("{{$repeat 'x 3}}", Now).ShouldBe("{{$repeat 'x 3}}");
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
