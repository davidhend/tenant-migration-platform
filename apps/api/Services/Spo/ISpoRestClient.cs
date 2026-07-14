namespace MigrationPlatform.Api.Services.Spo;

/// <summary>
/// Client for the SharePoint Online cross-tenant OneDrive migration cmdlets.
/// There is no public CSOM or REST API for the MnA cross-tenant OneDrive move flow;
/// Microsoft's only documented and supported surface is the
/// <c>Microsoft.Online.SharePoint.PowerShell</c> module. This interface is therefore
/// implemented by <see cref="SpoPowerShellClient"/>, which invokes <c>pwsh</c> as a
/// child process against the SPO Management Shell module.
/// </summary>
/// <remarks>
/// Prerequisites on the API host:
///   * PowerShell 7 (<c>pwsh</c>) on PATH
///   * <c>Install-Module Microsoft.Online.SharePoint.PowerShell -Scope AllUsers</c>
///   * Per-tenant app registration with a certificate uploaded and Sites.FullControl.All
/// </remarks>
public interface ISpoRestClient
{
    /// <summary>
    /// Start a cross-tenant OneDrive content move for a single user by invoking
    /// <c>Start-SPOCrossTenantUserContentMove</c> on the source tenant's admin endpoint.
    /// </summary>
    /// <param name="sourceAdminUrl">Source SPO admin URL (e.g. <c>https://contoso-admin.sharepoint.com</c>) — used by <c>Connect-SPOService</c>.</param>
    /// <param name="sourceUpn">UPN of the OneDrive owner on the source tenant.</param>
    /// <param name="targetUpn">UPN of the corresponding user on the target tenant.</param>
    /// <param name="targetCrossTenantHostUrl">Target tenant host URL returned by <c>Get-SPOCrossTenantHostUrl</c> (the <c>-my.sharepoint.com</c> host).</param>
    /// <param name="credentials">App-only certificate credentials for the source tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SpoMigrationJobResult> StartUserContentMoveAsync(
        string sourceAdminUrl,
        string sourceUpn,
        string targetUpn,
        string targetCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Get the current migration state for a user via
    /// <c>Get-SPOCrossTenantUserContentMoveState</c>. Returns <see langword="null"/> when no state is found.
    /// </summary>
    Task<SpoMigrationJobStatus?> GetUserContentMoveStateAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        string sourceUpn,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Start a cross-tenant SharePoint site content move by invoking
    /// <c>Start-SPOCrossTenantSiteContentMove</c> on the source tenant's admin endpoint.
    /// </summary>
    /// <param name="sourceAdminUrl">Source SPO admin URL.</param>
    /// <param name="sourceSiteUrl">Full URL of the source site (e.g. <c>https://contoso.sharepoint.com/sites/hr</c>).</param>
    /// <param name="targetSiteUrl">Full URL of the target site.</param>
    /// <param name="targetCrossTenantHostUrl">Target tenant host URL (<c>-my.sharepoint.com</c>).</param>
    /// <param name="credentials">App-only certificate credentials for the source tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SpoMigrationJobResult> StartSiteContentMoveAsync(
        string sourceAdminUrl,
        string sourceSiteUrl,
        string targetSiteUrl,
        string targetCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Get the current migration state for a site via
    /// <c>Get-SPOCrossTenantSiteContentMoveState</c>. Returns <see langword="null"/> when no state is found.
    /// </summary>
    Task<SpoMigrationJobStatus?> GetSiteContentMoveStateAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        string sourceSiteUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Get the migration state for a batch of users in ONE Automation runbook job
    /// (the runbook loops <c>Get-SPOCrossTenantUserContentMoveState</c> internally).
    /// Entries whose state is not registered yet come back with status <c>"NotFound"</c>;
    /// per-identity cmdlet failures come back with status <c>"Error"</c> and the message.
    /// </summary>
    Task<IReadOnlyList<SpoMigrationJobStatus>> GetUserContentMoveStatesAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        IReadOnlyCollection<string> sourceUpns,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Get the migration state for a batch of sites in ONE Automation runbook job
    /// (the runbook loops <c>Get-SPOCrossTenantSiteContentMoveState</c> internally).
    /// Same status conventions as <see cref="GetUserContentMoveStatesAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SpoMigrationJobStatus>> GetSiteContentMoveStatesAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        IReadOnlyCollection<string> sourceSiteUrls,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Resolve a tenant's canonical cross-tenant host URL via
    /// <c>Get-SPOCrossTenantHostUrl</c> (run against that tenant's admin URL with
    /// that tenant's credentials). This is the value partners must pass as
    /// <c>-TargetCrossTenantHostUrl</c> / <c>-PartnerCrossTenantHostUrl</c>.
    /// Returns null when the cmdlet yields nothing.
    /// </summary>
    Task<string?> GetCrossTenantHostUrlAsync(
        string adminUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Preflight check via <c>Get-SPOCrossTenantCompatibilityStatus</c>.
    /// Returns a <see cref="SpoCompatibilityResult"/> with the raw status and,
    /// when the check itself fails, the error message so the caller can surface
    /// a meaningful diagnostic to the user.
    /// </summary>
    Task<SpoCompatibilityResult> CheckCrossTenantCompatibilityAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Run <c>Set-SPOCrossTenantRelationship -Scenario MnA</c> followed by
    /// <c>Test-SPOCrossTenantRelationship</c> on ONE tenant (the one behind
    /// <paramref name="adminUrl"/>). <paramref name="partnerRole"/> is the role of
    /// the PARTNER tenant relative to this connection: the destination tenant
    /// passes <c>Source</c>, the source tenant passes <c>Target</c>.
    /// Returns the Test status (expected <c>GoodToProceed</c>).
    /// </summary>
    Task<string?> SetCrossTenantRelationshipAsync(
        string adminUrl,
        string partnerRole,
        string partnerCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Idempotently establish the MnA cross-tenant relationship between a tenant
    /// pair: check compatibility, and when not established run
    /// <c>Set-SPOCrossTenantRelationship</c> on the target side (PartnerRole
    /// <c>Source</c>) then the source side (PartnerRole <c>Target</c>) — the
    /// verified working order — and re-check. No state is persisted; idempotency
    /// comes from the compatibility check.
    /// </summary>
    /// <param name="skipPrecheck">Set true when the caller has already run the compatibility check and knows it failed (avoids one runbook round-trip).</param>
    Task<SpoRelationshipResult> EnsureCrossTenantRelationshipAsync(
        string sourceAdminUrl,
        string targetAdminUrl,
        string sourceCrossTenantHostUrl,
        string targetCrossTenantHostUrl,
        SpoPowerShellCredentials sourceCredentials,
        SpoPowerShellCredentials targetCredentials,
        bool skipPrecheck,
        CancellationToken ct);

    /// <summary>
    /// Upload a cross-tenant identity mapping CSV to the <strong>target</strong> tenant
    /// via <c>Add-SPOTenantIdentityMap</c>. Must be called before starting OneDrive
    /// content moves so SPO knows how source UPNs map to target UPNs.
    /// </summary>
    /// <param name="targetAdminUrl">Target SPO admin URL (e.g. <c>https://contoso-admin.sharepoint.com</c>).</param>
    /// <param name="identityMapCsvBase64">Base64-encoded CSV content (no headers, 6-column format).</param>
    /// <param name="credentials">App-only certificate credentials for the <strong>target</strong> tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UploadIdentityMapAsync(
        string targetAdminUrl,
        string identityMapCsvBase64,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);

    /// <summary>
    /// Pre-provision OneDrive personal sites for the given users on the target tenant by
    /// invoking <c>Request-SPOPersonalSite</c>. A Graph <c>GET /users/{upn}/drive</c> does
    /// NOT reliably trigger provisioning on its own — this cmdlet is the supported path.
    /// </summary>
    /// <param name="targetAdminUrl">Target SPO admin URL.</param>
    /// <param name="upns">Licensed target-tenant UPNs to pre-provision.</param>
    /// <param name="credentials">App-only certificate credentials for the <strong>target</strong> tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RequestPersonalSiteAsync(
        string targetAdminUrl,
        IEnumerable<string> upns,
        SpoPowerShellCredentials credentials,
        CancellationToken ct);
}
