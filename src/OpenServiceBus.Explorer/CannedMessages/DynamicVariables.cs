using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenServiceBus.Explorer.CannedMessages;

public static partial class DynamicVariables
{
    private const int MaxGeneratedLength = 1_048_576;
    private const int MaxBase64Bytes = 786_432;

    [GeneratedRegex(@"\{\{\$(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<args>[^{}]*)\}\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^(?<sign>[+-]?)(?<amount>\d+)(?<unit>[yMwdhms])$")]
    private static partial Regex OffsetRegex();

    private static readonly ConcurrentDictionary<string, long> Sequences = new();

    public static bool ContainsVariables(string? value) =>
        !string.IsNullOrEmpty(value) && TokenRegex().IsMatch(value);

    public static void ResetSequences() => Sequences.Clear();

    public static string? Resolve(string? template, DateTimeOffset now, int copyIndex = 0, string sequenceScope = "")
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{$", StringComparison.Ordinal))
        {
            return template;
        }

        return TokenRegex().Replace(template, match =>
        {
            if (TryParseArgs(match.Groups["args"].Value) is not { } parsed) return match.Value;
            var args = parsed.Select(a => a.Value).ToArray();
            return match.Groups["name"].Value.ToLowerInvariant() switch
            {
                "guid" => ResolveGuid(args, match.Value),
                "ulid" => args.Length == 0 ? NewUlid(now) : match.Value,
                "sequence" => ResolveSequence(args, sequenceScope, template!, match.Value),
                "index" => args.Length == 0 ? copyIndex.ToString(CultureInfo.InvariantCulture) : match.Value,
                "datetime" => ResolveDateTime(parsed, now, match.Value),
                "timestamp" => args.Length == 0 ? now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : match.Value,
                "randomint" => ResolveRandomInt(args, match.Value),
                "randomdouble" => ResolveRandomDouble(args, match.Value),
                "randomboolean" => args.Length == 0 ? (Random.Shared.Next(2) == 0 ? "false" : "true") : match.Value,
                "randomalphanumeric" => ResolveRandomString(args, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", match.Value),
                "randomhex" => ResolveRandomString(args, "0123456789abcdef", match.Value),
                "randomchoice" => ResolveRandomChoice(match.Groups["args"].Value, match.Value),
                "randombase64" => ResolveRandomBase64(args, match.Value),
                "repeat" => ResolveRepeat(args, match.Value),
                _ => match.Value,
            };
        });
    }

    /// <summary>
    /// Splits an argument string on whitespace, honoring single-quoted segments so
    /// arguments like a .NET datetime format may contain spaces; each argument keeps a
    /// flag telling whether it was quoted (custom datetime formats require the quoted
    /// form). Returns null on unbalanced quotes, which leaves the whole token verbatim.
    /// </summary>
    private static List<(string Value, bool Quoted)>? TryParseArgs(string raw)
    {
        var args = new List<(string, bool)>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasCurrent = false;
        var wasQuoted = false;
        foreach (var c in raw)
        {
            if (c == '\'')
            {
                inQuotes = !inQuotes;
                hasCurrent = true;
                wasQuoted = true;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasCurrent) args.Add((current.ToString(), wasQuoted));
                current.Clear();
                hasCurrent = false;
                wasQuoted = false;
                continue;
            }
            current.Append(c);
            hasCurrent = true;
        }
        if (inQuotes) return null;
        if (hasCurrent) args.Add((current.ToString(), wasQuoted));
        return args;
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

    private static string NewUlid(DateTimeOffset now)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<byte> bytes = stackalloc byte[16];
        var ms = now.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }
        RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> chars = stackalloc char[26];
        var buffer = 0uL;
        var bits = 0;
        var position = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[position++] = alphabet[(int)((buffer >> bits) & 0x1F)];
            }
        }
        chars[position] = alphabet[(int)((buffer << (5 - bits)) & 0x1F)];
        return new string(chars);
    }

    private static string ResolveSequence(string[] args, string scope, string template, string verbatim)
    {
        long start = 1;
        if (args.Length > 1) return verbatim;
        if (args.Length == 1 && !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) return verbatim;

        var key = $"{scope}|{template.GetHashCode(StringComparison.Ordinal)}|{start}";
        var value = Sequences.AddOrUpdate(key, start, (_, current) => current + 1);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string ResolveDateTime(List<(string Value, bool Quoted)> parsed, DateTimeOffset now, string verbatim)
    {
        var args = parsed.Select(a => a.Value).ToArray();
        if (args.Length is 0 or > 2) return verbatim;

        var at = now;
        if (args.Length == 2)
        {
            var offset = OffsetRegex().Match(args[1]);
            if (!offset.Success) return verbatim;
            var amount = int.Parse(offset.Groups["amount"].Value, CultureInfo.InvariantCulture);
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

        switch (args[0].ToLowerInvariant())
        {
            case "iso8601": return at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            case "rfc1123": return at.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
            case "unix": return at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "unixms": return at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (!parsed[0].Quoted) return verbatim;
        try
        {
            var formatted = at.UtcDateTime.ToString(args[0], CultureInfo.InvariantCulture);
            return formatted.Length == 0 ? verbatim : formatted;
        }
        catch (FormatException)
        {
            return verbatim;
        }
    }

    private static string ResolveRandomInt(string[] args, string verbatim)
    {
        long min = 0, max = 1000;
        switch (args.Length)
        {
            case 0:
                break;
            case 2 when long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out min)
                     && long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out max)
                     && min <= max:
                break;
            default:
                return verbatim;
        }
        return Random.Shared.NextInt64(min, max == long.MaxValue ? max : max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string ResolveRandomDouble(string[] args, string verbatim)
    {
        if (args.Length is < 2 or > 3) return verbatim;
        if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
            || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
            || min > max)
        {
            return verbatim;
        }
        var decimals = 2;
        if (args.Length == 3 && (!int.TryParse(args[2], out decimals) || decimals is < 0 or > 15)) return verbatim;
        var value = min + Random.Shared.NextDouble() * (max - min);
        return Math.Round(value, decimals).ToString($"F{decimals}", CultureInfo.InvariantCulture);
    }

    private static string ResolveRandomString(string[] args, string alphabet, string verbatim)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var length) || length is < 1 or > MaxGeneratedLength) return verbatim;
        return string.Create(length, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[Random.Shared.Next(chars.Length)];
            }
        });
    }

    private static string ResolveRandomChoice(string rawArgs, string verbatim)
    {
        var choices = rawArgs.Trim().Split('|', StringSplitOptions.TrimEntries);
        if (choices.Length < 2 || choices.Any(c => c.Length == 0)) return verbatim;
        return choices[Random.Shared.Next(choices.Length)];
    }

    private static string ResolveRandomBase64(string[] args, string verbatim)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var bytes) || bytes is < 1 or > MaxBase64Bytes) return verbatim;
        var blob = new byte[bytes];
        RandomNumberGenerator.Fill(blob);
        return Convert.ToBase64String(blob);
    }

    private static string ResolveRepeat(string[] args, string verbatim)
    {
        if (args.Length != 2 || args[0].Length == 0 || !int.TryParse(args[1], out var times) || times < 1) return verbatim;
        if ((long)args[0].Length * times > MaxGeneratedLength) return verbatim;
        return string.Concat(Enumerable.Repeat(args[0], times));
    }
}
