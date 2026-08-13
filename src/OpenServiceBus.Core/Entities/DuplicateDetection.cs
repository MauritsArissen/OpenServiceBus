namespace OpenServiceBus.Core.Entities;

/// <summary>
/// Shared duplicate-detection window resolution used by every send path (queue single/batch,
/// topic single/batch, $management schedule) so the "null window means 10 minutes" default
/// lives in exactly one place.
/// </summary>
public static class DuplicateDetection
{
    /// <summary>Azure's default history window when the entity enables dedup without one.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The window to check against, or null when the entity does not require duplicate
    /// detection (null disables the check entirely on the store).
    /// </summary>
    public static TimeSpan? EffectiveWindow(bool requiresDuplicateDetection, TimeSpan? configuredWindow) =>
        requiresDuplicateDetection ? configuredWindow ?? DefaultWindow : null;
}
