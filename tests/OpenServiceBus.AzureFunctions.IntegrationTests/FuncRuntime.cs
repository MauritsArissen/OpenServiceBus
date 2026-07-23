using System.Diagnostics;

namespace OpenServiceBus.AzureFunctions.IntegrationTests;

/// <summary>
/// Helpers for discovering the Azure Functions Core Tools (<c>func</c>) and the .NET 8 runtime
/// required to host the sample isolated-worker app. Tests use these to skip gracefully when
/// the local environment can't run the gate, and to inject the right <c>DOTNET_ROOT</c>/<c>PATH</c>
/// into the spawned <c>func</c> process when .NET 8 is installed alongside .NET 10.
/// </summary>
internal static class FuncRuntime
{
    public static string? FindFunc()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var exeName = OperatingSystem.IsWindows() ? "func.exe" : "func";
        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the root of a .NET 8 installation usable as <c>DOTNET_ROOT</c>, searching:
    ///   1) the <c>dotnet</c> on PATH (if it ships .NET 8 alongside),
    ///   2) Homebrew keg-only formula <c>dotnet@8</c>,
    ///   3) the Microsoft system install location.
    /// Returns null when no usable .NET 8 host is found.
    /// </summary>
    public static string? DiscoverDotNet8Root()
    {
        if (RuntimePresentIn(GetDefaultDotnetRoot()))
        {
            return GetDefaultDotnetRoot();
        }

        string[] candidates =
        {
            "/opt/homebrew/opt/dotnet@8/libexec",
            "/usr/local/opt/dotnet@8/libexec",
            "/usr/local/share/dotnet",
            "/usr/share/dotnet",
        };
        foreach (var c in candidates)
        {
            if (RuntimePresentIn(c))
            {
                return c;
            }
        }
        return null;
    }

    private static bool RuntimePresentIn(string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var dir = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(dir)) return false;
        return Directory.EnumerateDirectories(dir, "8.*").Any();
    }

    private static string? GetDefaultDotnetRoot()
    {
        // Resolve the directory of `dotnet` on PATH; its parent is DOTNET_ROOT.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                // Resolve symlinks (e.g. /opt/homebrew/bin/dotnet → ../Cellar/...)
                var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                var binDir = Path.GetDirectoryName(resolved);
                if (binDir is null) continue;

                // brew's dotnet sits in <cellar>/libexec/dotnet, with shared/ as a sibling of dotnet.
                if (Directory.Exists(Path.Combine(binDir, "shared")))
                {
                    return binDir;
                }
                // Microsoft installer layout: dotnet is in <root>/, shared is <root>/shared.
                var parent = Path.GetDirectoryName(binDir);
                if (parent is not null && Directory.Exists(Path.Combine(parent, "shared")))
                {
                    return parent;
                }
            }
        }
        return null;
    }

    public static string ResolveSampleProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "dotnet", "OpenServiceBus.Samples.Functions");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate samples/dotnet/OpenServiceBus.Samples.Functions from the test assembly directory.");
    }
}
