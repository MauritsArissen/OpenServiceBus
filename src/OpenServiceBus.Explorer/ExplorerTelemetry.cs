using System.Diagnostics;

namespace OpenServiceBus.Explorer;

/// <summary>
/// The Explorer's own <see cref="ActivitySource"/> for operations that are Explorer
/// features rather than plain SDK passthroughs (e.g. resend). No-op unless a listener
/// (OpenTelemetry SDK, dotnet-trace) subscribes to "OpenServiceBus.Explorer".
/// </summary>
public static class ExplorerTelemetry
{
    public static readonly ActivitySource Source = new("OpenServiceBus.Explorer");
}
