namespace MigrationPlatform.Api.Services;

/// <summary>
/// Which side (and which UPN list) the Cross Tenant User Data Migration license
/// is assigned on. Pure so the side-selection is unit-testable independent of the
/// worker's DI/Graph plumbing.
/// </summary>
public static class LicenseAssignmentPolicy
{
    /// <summary>
    /// Returns the UPN list to license: the SOURCE UPNs when
    /// <paramref name="side"/> is "source" (case-insensitive), otherwise the
    /// TARGET UPNs. Anything other than "source" defaults to target, matching
    /// the LicenseAssignmentSide default.
    /// </summary>
    public static bool AssignOnSource(string? side)
        => string.Equals((side ?? "target").Trim(), "source", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Backstop for OneDrive pre-provisioning: how many consecutive
/// Request-SPOPersonalSite Automation-job FAILURES before the content job is
/// marked Failed instead of silently resubmitting.
/// </summary>
public static class ProvisionRetryPolicy
{
    /// <summary>
    /// True when the failure count has reached the cap and the job should be
    /// marked Failed. The cap is clamped to at least 1 so a misconfigured
    /// non-positive value fails fast rather than looping forever.
    /// </summary>
    public static bool ShouldFail(int consecutiveFailures, int maxAttempts)
        => consecutiveFailures >= Math.Max(1, maxAttempts);
}
