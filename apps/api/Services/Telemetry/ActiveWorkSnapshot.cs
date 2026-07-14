namespace MigrationPlatform.Api.Services.Telemetry;

/// <summary>
/// Point-in-time counts of active (non-terminal) migration work, refreshed
/// periodically by <see cref="ActiveWorkMetrics"/> and read by the observable
/// gauges on <see cref="PlatformMetrics"/> and by the stuck-jobs health check.
/// Immutable so it can be swapped atomically without locking.
/// </summary>
public sealed record ActiveWorkSnapshot(
    IReadOnlyList<KeyValuePair<string, long>> CountsByKind,
    long StuckCount,
    IReadOnlyList<string> StuckDescriptions)
{
    public static readonly ActiveWorkSnapshot Empty =
        new(Array.Empty<KeyValuePair<string, long>>(), 0, Array.Empty<string>());
}
