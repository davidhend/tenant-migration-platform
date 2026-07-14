using MigrationPlatform.Api.Services.Telemetry;

namespace MigrationPlatform.Api.Tests;

public class TelemetryPolicyTests
{
    // ── RetentionPolicy.CutoffUtc ───────────────────────────────────────────

    [Fact]
    public void CutoffUtc_subtracts_retention_days()
    {
        var now = new DateTime(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddDays(-365), RetentionPolicy.CutoffUtc(now, 365));
        Assert.Equal(now.AddDays(-30), RetentionPolicy.CutoffUtc(now, 30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CutoffUtc_clamps_nonpositive_to_at_least_one_day(int days)
    {
        // Guard: retention can never be configured to "delete everything up to now".
        var now = new DateTime(2026, 07, 04, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddDays(-1), RetentionPolicy.CutoffUtc(now, days));
    }

    // ── StuckJobPolicy.IsStuck ──────────────────────────────────────────────

    [Fact]
    public void IsStuck_true_when_progress_older_than_threshold()
    {
        var now = new DateTime(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(StuckJobPolicy.IsStuck(now.AddHours(-7), now, 6));
    }

    [Fact]
    public void IsStuck_false_when_within_threshold()
    {
        var now = new DateTime(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(StuckJobPolicy.IsStuck(now.AddHours(-5), now, 6));
        Assert.False(StuckJobPolicy.IsStuck(now.AddMinutes(-359), now, 6)); // just under 6h
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsStuck_disabled_when_threshold_nonpositive(int threshold)
    {
        // A non-positive threshold disables detection rather than flagging everything.
        var now = new DateTime(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(StuckJobPolicy.IsStuck(now.AddDays(-30), now, threshold));
    }
}
