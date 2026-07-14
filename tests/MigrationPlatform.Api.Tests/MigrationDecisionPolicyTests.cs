using MigrationPlatform.Api.Services;
using Xunit;

namespace MigrationPlatform.Api.Tests;

public class LicenseAssignmentPolicyTests
{
    [Theory]
    [InlineData("source", true)]
    [InlineData("Source", true)]
    [InlineData("SOURCE", true)]
    [InlineData("  source  ", true)]
    [InlineData("target", false)]
    [InlineData("Target", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("anything-else", false)]
    public void AssignOnSource_selects_source_only_for_source(string? side, bool expected)
        => Assert.Equal(expected, LicenseAssignmentPolicy.AssignOnSource(side));
}

public class ProvisionRetryPolicyTests
{
    [Theory]
    [InlineData(1, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]   // at the cap → fail
    [InlineData(6, 5, true)]   // past the cap → fail
    public void ShouldFail_at_or_past_the_cap(int failures, int max, bool expected)
        => Assert.Equal(expected, ProvisionRetryPolicy.ShouldFail(failures, max));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ShouldFail_clamps_nonpositive_cap_to_one(int badMax)
    {
        // A misconfigured non-positive cap must fail fast (>=1), not loop forever.
        Assert.False(ProvisionRetryPolicy.ShouldFail(0, badMax));
        Assert.True(ProvisionRetryPolicy.ShouldFail(1, badMax));
    }
}
