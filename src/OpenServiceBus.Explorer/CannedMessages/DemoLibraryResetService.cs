namespace OpenServiceBus.Explorer.CannedMessages;

/// <summary>
/// Hosted-demo companion to the broker's seeder reset: on the same wall-clock boundaries
/// (OSB_EXPLORER_RESET_INTERVAL_SECONDS, default 1800) the canned message library is restored
/// to its configured defaults, discarding whatever demo visitors added. Runs only when
/// OSB_EXPLORER_DEMO=true; a normal Explorer never resets anything.
/// </summary>
public sealed class DemoLibraryResetService(CannedMessageLibrary library, ILogger<DemoLibraryResetService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("OSB_EXPLORER_DEMO"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var interval = int.TryParse(Environment.GetEnvironmentVariable("OSB_EXPLORER_RESET_INTERVAL_SECONDS"), out var s) && s > 0
            ? s
            : 1800;

        var lastBoundary = Boundary(interval);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var boundary = Boundary(interval);
                if (boundary == lastBoundary) continue;
                lastBoundary = boundary;
                library.ResetToDefaults();
                logger.LogInformation("Demo reset: canned message library restored to defaults");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static long Boundary(int intervalSeconds) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() / intervalSeconds;
}
