namespace MigrationPlatform.Api.Services.Telemetry;

/// <summary>Pure, testable policy helpers for retention and stuck-job detection.</summary>
public static class RetentionPolicy
{
    /// <summary>
    /// Records with a timestamp at or before this cutoff are eligible for deletion.
    /// A non-positive <paramref name="retentionDays"/> is clamped to at least 1 day
    /// so retention can never be configured to delete "everything up to now".
    /// </summary>
    public static DateTime CutoffUtc(DateTime nowUtc, int retentionDays)
        => nowUtc - TimeSpan.FromDays(Math.Max(1, retentionDays));
}

/// <summary>Stuck-job threshold evaluation.</summary>
public static class StuckJobPolicy
{
    /// <summary>
    /// True when an active item's last progress is older than the threshold.
    /// A non-positive <paramref name="thresholdHours"/> disables detection
    /// (nothing is ever considered stuck) rather than flagging everything.
    /// </summary>
    public static bool IsStuck(DateTime lastProgressUtc, DateTime nowUtc, int thresholdHours)
    {
        if (thresholdHours <= 0) return false;
        return nowUtc - lastProgressUtc > TimeSpan.FromHours(thresholdHours);
    }
}
