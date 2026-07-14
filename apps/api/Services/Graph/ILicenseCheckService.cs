using Microsoft.Graph;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Verifies that target users have the required Microsoft 365 service plans
/// before launching workload migrations. OneDrive provisioning needs an active
/// SharePoint Online service plan; mailbox migration needs an active Exchange
/// Online service plan. Both fail silently or after long delays if the user is
/// unlicensed, so we surface a clear error up front.
/// </summary>
public interface ILicenseCheckService
{
    /// <summary>
    /// Check the given UPNs on the target tenant for an enabled SharePoint Online
    /// (OneDrive-bearing) service plan. Returns the subset of UPNs that are NOT
    /// licensed, paired with a short reason message.
    /// </summary>
    Task<IReadOnlyList<LicenseCheckResult>> CheckOneDriveLicensesAsync(
        GraphServiceClient client,
        IEnumerable<string> upns,
        CancellationToken ct);

    /// <summary>
    /// Check the given UPNs on the target tenant for an enabled Exchange Online
    /// service plan. Returns the subset of UPNs that are NOT licensed.
    /// </summary>
    Task<IReadOnlyList<LicenseCheckResult>> CheckExchangeLicensesAsync(
        GraphServiceClient client,
        IEnumerable<string> upns,
        CancellationToken ct);

    /// <summary>
    /// Ensure every given UPN carries the "Cross Tenant User Data Migration"
    /// add-on license (required for cross-tenant mailbox AND OneDrive moves;
    /// assignable on either the source or target side — the caller picks the
    /// side by passing that tenant's Graph client and that side's UPNs).
    /// Looks up the SKU on the tenant's subscribedSkus, skips users that
    /// already have it, and assigns it to the rest via <c>assignLicense</c>.
    /// Requires the app permission <c>User.ReadWrite.All</c> (plus
    /// <c>Organization.Read.All</c>/<c>Directory.Read.All</c> to read SKUs).
    /// Never throws for per-user problems — failures are reported per UPN.
    /// </summary>
    Task<CrossTenantLicenseEnsureResult> EnsureCrossTenantMigrationLicensesAsync(
        GraphServiceClient client,
        IEnumerable<string> upns,
        string defaultUsageLocation,
        CancellationToken ct);
}

/// <summary>One UPN's license verdict.</summary>
public sealed record LicenseCheckResult(string Upn, bool HasLicense, string? Reason);

/// <summary>One UPN that could not be licensed, with the Graph error.</summary>
public sealed record LicenseAssignmentFailure(string Upn, string Reason);

/// <summary>
/// Outcome of <see cref="ILicenseCheckService.EnsureCrossTenantMigrationLicensesAsync"/>.
/// <paramref name="SeatsAvailable"/> is prepaid-enabled minus consumed at the time of
/// the call (before this call's assignments); <paramref name="UsageLocationsSet"/> lists
/// users whose missing <c>usageLocation</c> was defaulted so the assignment could proceed.
/// </summary>
public sealed record CrossTenantLicenseEnsureResult(
    bool SkuFound,
    string? SkuId,
    string? SkuPartNumber,
    int SeatsAvailable,
    int SeatsNeeded,
    IReadOnlyList<string> Assigned,
    IReadOnlyList<string> AlreadyLicensed,
    IReadOnlyList<LicenseAssignmentFailure> Failed,
    IReadOnlyList<string> UsageLocationsSet);
