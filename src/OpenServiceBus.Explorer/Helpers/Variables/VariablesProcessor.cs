using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenServiceBus.Explorer.Helpers.Variables;

public partial class VariablesProcessor
{
    [GeneratedRegex(
        @"^.*(?<variableBlock>\{\{\$(?<variable>\w+)(?<flag1> [a-zA-Z0-9|-]+)?(?<flag1> [a-zA-Z0-9|-]+)?(?<flag2> \w+)?(?<option> \[\w+:\w+\])*\}\}).*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex GetVariablesRegex();

    private const string VariableBlockKey = "variableBlock";
    private const string VariableKey = "variable";
    private const string Flag1 = "flag1";
    private const string Flag2 = "flag2";
    private const string Option = "option";

    public static string Process(string payload)
    {
        var sb = new StringBuilder(payload.Length);
        var lastIndex = 0;
        ReadOnlySpan<char> payloadSpan = payload;

        List<IVariableProcessor> processors = [new GuidVariableProcessor(), new DateTimeVariableProcessor()];

        foreach (Match match in GetVariablesRegex().Matches(payload))
        {
            // Variable
            if (!match.Groups.TryGetValue(VariableBlockKey, out var variableBlock))
            {
                continue;
            }

            if (!match.Groups.TryGetValue(VariableKey, out var variable))
            {
                continue;
            }

            // Flag1 and Flag2
            List<string> implicitOption = [];
            if (match.Groups.TryGetValue(Flag1, out var flag1) && flag1.Success)
            {
                implicitOption.Add(flag1.Value);
            }

            if (match.Groups.TryGetValue(Flag2, out var flag2) && flag2.Success)
            {
                implicitOption.Add(flag2.Value);
            }

            // Process value
            string? computedValue = null;
            foreach (var processor in processors)
            {
                if (processor.TryProcess(variable.Value, match.Value, implicitOption, [], out computedValue))
                {
                    // Stop at first processor that handles it
                    break;
                }
            }

            if (computedValue is null)
            {
                // No processor matched — leave this occurrence untouched
                continue;
            }

            // Append/replace line and computed variable value
            sb.Append(payloadSpan.Slice(lastIndex, variableBlock.Index - lastIndex));
            sb.Append(computedValue ?? variableBlock.Value);

            lastIndex = variableBlock.Index + variableBlock.Length;
        }

        sb.Append(payloadSpan.Slice(lastIndex));

        var result = sb.ToString();
        return result;
    }
}

public interface IVariableProcessor
{
    string Token {  get; }

    Dictionary<string, string> Options { get; }

    bool TryProcess(string variable, string line, List<string> implicitOption, Dictionary<string, string> options, out string? computedValue);

    string Process(string line, List<string> implicitOption, Dictionary<string, string> options);
}

public abstract class VariableProcessorBase : IVariableProcessor
{
    public abstract string Token { get; }

    public Dictionary<string, string> Options { get; protected set; } = [];

    public abstract string Process(string line, List<string> implicitOption, Dictionary<string, string> options);

    public bool TryProcess(string variable, string line, List<string> implicitOptions, Dictionary<string, string> options, out string? value)
    {
        value = null;
        if (string.Equals(variable.ToUpperInvariant(), Token))
        {
            value = this.Process(line, implicitOptions, options);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Transforms a {{$guid}} token to a generated <see cref="Guid"/>.
/// <para>
/// By default, the resulting string will be lowercase.
/// </para>
/// <para>
/// Implicit option: UPPER|LOWER
/// </para>
/// <para>
/// Options: none
/// </para>
/// </summary>
public class GuidVariableProcessor : VariableProcessorBase
{
    public override string Token => "GUID";

    public override string Process(string line, List<string> implicitOptions, Dictionary<string, string> options)
    {
        string value = Guid.NewGuid().ToString();

        if (implicitOptions.Count > 0)
        {
            var casing = implicitOptions[0].Trim().ToUpperInvariant();
            value = casing switch
            {
                "UPPER" => value.ToUpperInvariant(),
                "LOWER" => value.ToLowerInvariant(),
                _ => value
            };
        }

        return value;
    }
}

/// <summary>
/// Transforms a {{$datetime}} token to a <see cref="DateTime"/> string.
/// <para>
/// Implicit option:
/// - RFC1123 | ISO8601
/// - offset (format: +/- offset y/d/h/m; example: 1d, +2d, -5d; -1h)
/// </para>
/// <para>
/// Options: none
/// </para>
/// </summary>
public partial class DateTimeVariableProcessor : VariableProcessorBase
{
    public const string DefaultFormat = "yyyy/MM/dd HH:mm:ss";
    public const string Rfc1123Format = "R";
    public const string Iso8601Format = "yyyy-MM-ddTHH:mm:ssZ";

    public override string Token => "DATETIME";

    [GeneratedRegex(
        @"^(?<sign>[+-]?)(?<value>\d+)(?<unit>[ydhm])$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant)]
    private static partial Regex GetOffsetRegex();

    public override string Process(string line, List<string> implicitOptions, Dictionary<string, string> options)
    {
        DateTime dateTime = DateTime.UtcNow;
        string format = DefaultFormat;

        // apply options
        if (implicitOptions.Count > 0)
        {
            if (implicitOptions.Count == 1)
            {
                var implicitOption = implicitOptions[0].Trim();

                // check which flag was passed; either the format or the offset
                var offsetMatch = GetOffsetRegex().Match(implicitOption);
                if (offsetMatch.Success)
                {
                    dateTime = ApplyOffset(dateTime, implicitOption, offsetMatch);
                }
                else
                {
                    format = implicitOption.ToUpperInvariant();
                }
            }

            if (implicitOptions.Count == 2)
            {
                format = implicitOptions[0].Trim().ToUpperInvariant();
                dateTime = ApplyOffset(dateTime, implicitOptions[0]);
            }
        }

        // format datetime to string
        string formattedDate = format switch
        {
            "RFC1123" => dateTime.ToString(Rfc1123Format, CultureInfo.InvariantCulture),
            "ISO8601" => dateTime.ToString(Iso8601Format, CultureInfo.InvariantCulture),
            _ => dateTime.ToString(format)
        };

        return formattedDate;
    }

    private static DateTime ApplyOffset(DateTime dateTime, string offset, Match? match = null)
    {
        if (match is null)
        {
            match = GetOffsetRegex().Match(offset);
            if (!match.Success)
            {
                throw new FormatException($"Invalid offset format: '{offset}'");
            }
        }

        var value = int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["sign"].Value == "-")
        {
            value = -value;
        }

        return match.Groups["unit"].Value switch
        {
            "y" => dateTime.AddYears(value),
            "d" => dateTime.AddDays(value),
            "h" => dateTime.AddHours(value),
            "m" => dateTime.AddMinutes(value),
            _ => throw new FormatException($"Unsupported unit in offset: '{offset}'")
        };
    }
}
