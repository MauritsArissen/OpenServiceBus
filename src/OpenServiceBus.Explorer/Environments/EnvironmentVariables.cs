using System.Text.RegularExpressions;

namespace OpenServiceBus.Explorer.Environments;

/// <summary>
/// Postman-style environment substitution: plain {{name}} tokens are replaced from the
/// active environment's ENABLED values. The {{$...}} namespace is never touched here -
/// it belongs to the built-in dynamic variables, which run after this pass, so an
/// environment value may itself contain {{$guid}} and still resolve per message copy.
/// Unresolved names stay verbatim, matching Postman.
/// </summary>
public static partial class EnvironmentVariables
{
    [GeneratedRegex(@"\{\{(?!\$)\s*(?<name>[^{}\s]+)\s*\}\}")]
    private static partial Regex TokenRegex();

    public static string? Resolve(string? template, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0
            || string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
        {
            return template;
        }

        return TokenRegex().Replace(template, match =>
            values.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);
    }

    public static Dictionary<string, string> EnabledValues(ExplorerEnvironment environment)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in environment.Values.Where(v => v.Enabled && !string.IsNullOrEmpty(v.Key)))
        {
            map[value.Key] = value.Value;
        }
        return map;
    }
}
