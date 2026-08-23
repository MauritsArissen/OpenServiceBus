using System.Text.RegularExpressions;

namespace OpenServiceBus.Explorer.CannedMessages;

public static partial class DynamicVariables
{
    [GeneratedRegex(@"\{\{\$(?<name>[a-zA-Z]+)(?<args>(?:\s+[^\s{}]+)*)\s*\}\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^(?<sign>[+-]?)(?<amount>\d+)(?<unit>[yMwdhms])$")]
    private static partial Regex OffsetRegex();

    public static bool ContainsVariables(string? value) =>
        !string.IsNullOrEmpty(value) && TokenRegex().IsMatch(value);

    public static string? Resolve(string? template, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{$", StringComparison.Ordinal))
        {
            return template;
        }

        return TokenRegex().Replace(template, match =>
        {
            var args = match.Groups["args"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return match.Groups["name"].Value.ToLowerInvariant() switch
            {
                "guid" => ResolveGuid(args, match.Value),
                "datetime" => ResolveDateTime(args, now, match.Value),
                _ => match.Value,
            };
        });
    }

    private static string ResolveGuid(string[] args, string verbatim)
    {
        var value = Guid.NewGuid().ToString("D");
        return args switch
        {
            [] => value,
            ["upper"] => value.ToUpperInvariant(),
            ["lower"] => value,
            _ => verbatim,
        };
    }

    private static string ResolveDateTime(string[] args, DateTimeOffset now, string verbatim)
    {
        if (args.Length is 0 or > 2) return verbatim;

        var at = now;
        if (args.Length == 2)
        {
            var offset = OffsetRegex().Match(args[1]);
            if (!offset.Success) return verbatim;
            var amount = int.Parse(offset.Groups["amount"].Value);
            if (offset.Groups["sign"].Value == "-") amount = -amount;
            at = offset.Groups["unit"].Value switch
            {
                "y" => at.AddYears(amount),
                "M" => at.AddMonths(amount),
                "w" => at.AddDays(7L * amount),
                "d" => at.AddDays(amount),
                "h" => at.AddHours(amount),
                "m" => at.AddMinutes(amount),
                _ => at.AddSeconds(amount),
            };
        }

        return args[0].ToLowerInvariant() switch
        {
            "iso8601" => at.UtcDateTime.ToString("o"),
            "rfc1123" => at.UtcDateTime.ToString("r"),
            _ => verbatim,
        };
    }
}
