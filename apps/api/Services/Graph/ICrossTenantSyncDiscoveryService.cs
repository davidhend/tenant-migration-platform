using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Result of probing the target tenant for an Entra cross-tenant
/// synchronization configuration that points at the source tenant. All fields
/// are best-effort — Graph access errors are reported via <see cref="Error"/>
/// rather than thrown so callers can surface the issue without crashing.
/// </summary>
/// <param name="IsConfigured">
/// True only when a synchronization job with a cross-tenant sync template was
/// found in the target tenant AND its secret <c>BaseAddress</c> binds it to the
/// source tenant ID. When true, <c>provisionOnDemand</c> against the discovered
/// service principal/job pair will work for source users.
/// </param>
/// <param name="PartnerPolicyConfigured">
/// True when <c>/policies/crossTenantAccessPolicy/partners/{sourceTenantId}</c>
/// exists in the target tenant. Required for any cross-tenant collaboration —
/// missing it means the sync app cannot authenticate against the source.
/// </param>
/// <param name="ServicePrincipalId">Enterprise app object ID hosting the sync job (when found).</param>
/// <param name="ServicePrincipalDisplayName">Display name of the enterprise app (when found).</param>
/// <param name="SyncJobId">Synchronization job ID (when found).</param>
/// <param name="SyncJobTemplateId">Template ID of the discovered job (e.g. <c>Azure2Azure</c>).</param>
/// <param name="SyncJobStatus">Status code reported by the job (e.g. <c>NotRun</c>, <c>Active</c>, <c>Paused</c>).</param>
/// <param name="LastSyncAt">Timestamp of the last successful sync, when reported by the job.</param>
/// <param name="Message">Human-readable summary suitable for the dependency-check detail line.</param>
/// <param name="Remediation">When not configured, the next step to take in the target tenant.</param>
/// <param name="Error">Populated if Graph access failed — distinguishes "not configured" from "could not check".</param>
public sealed record CrossTenantSyncDiscoveryResult(
    bool IsConfigured,
    bool PartnerPolicyConfigured,
    string? ServicePrincipalId,
    string? ServicePrincipalDisplayName,
    string? SyncJobId,
    string? SyncJobTemplateId,
    string? SyncJobStatus,
    DateTimeOffset? LastSyncAt,
    string Message,
    string? Remediation,
    string? Error,
    IReadOnlyList<CrossTenantSyncCandidate>? Candidates = null);

/// <summary>
/// A service principal that matched the cross-tenant sync display-name pattern
/// during discovery, whether or not its synchronization jobs could be read.
/// Surfaced so an admin can identify which app registration needs the missing
/// Graph permission without digging through debug logs.
/// </summary>
public sealed record CrossTenantSyncCandidate(
    string? DisplayName,
    string? AppId,
    string? ServicePrincipalId);

/// <summary>
/// Discovers whether the target tenant has a cross-tenant synchronization
/// configuration set up for the source tenant. Used by the dependency-check
/// endpoint and by the project overview discovery card to upgrade the
/// "cross-tenant-sync-app" advisory from a generic warning to pass/fail.
/// </summary>
public interface ICrossTenantSyncDiscoveryService
{
    /// <summary>
    /// Probe the target tenant for cross-tenant sync configuration that
    /// targets the source tenant. Returns a synthetic "configured" result in
    /// mock mode so the UI flow can be exercised without real credentials.
    /// </summary>
    Task<CrossTenantSyncDiscoveryResult> DiscoverAsync(
        Tenant sourceTenant,
        Tenant targetTenant,
        CancellationToken ct = default);
}
