namespace MigrationPlatform.Api.Models;

public enum UserMigrationBatchStatus { Draft, Provisioning, Completed, Failed, Stopped }

/// <summary>
/// Selects how source users are materialised in the target tenant.
/// <para><c>DirectGraph</c> — Graph <c>POST /users</c> per entry. Plain member
/// accounts with a fresh password (forced reset on first sign-in). No Entra
/// dependency in either tenant. Best when source identities aren't federated
/// or the target tenant doesn't have CTS configured.</para>
/// <para><c>CrossTenantSync</c> — Entra ID cross-tenant synchronization
/// (<c>provisionOnDemand</c> on the cross-tenant sync app). Source users keep
/// their home credentials and appear in the target tenant as synced identities.
/// Requires the cross-tenant sync app + sync job set up in the target tenant
/// and the partner cross-tenant access policy enabled on both sides.</para>
/// </summary>
public enum UserMigrationStrategy { DirectGraph, CrossTenantSync }

/// <summary>
/// A batch of source→target UPN pairs that will be materialised in the target
/// tenant. The transport is chosen per batch via <see cref="Strategy"/> — see
/// <see cref="UserMigrationStrategy"/> for the tradeoffs.
/// </summary>
public class UserMigrationBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public MigrationProject? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public UserMigrationBatchStatus Status { get; set; } = UserMigrationBatchStatus.Draft;

    /// <summary>Selected transport — DirectGraph (default) or CrossTenantSync.</summary>
    public UserMigrationStrategy Strategy { get; set; } = UserMigrationStrategy.DirectGraph;

    public int TotalUsers { get; set; }
    public int ProvisionedUsers { get; set; }
    public int FailedUsers { get; set; }

    /// <summary>
    /// Entries that were never attempted (e.g. unmappable target). Excluded
    /// from both progress denominator and the "all failed → Failed" rule so
    /// batch status reflects only users that were actually provisioned.
    /// </summary>
    public int SkippedUsers { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// CrossTenantSync only — composite ID <c>{servicePrincipalObjectId}/{jobId}</c>
    /// of the source-tenant Azure2Azure synchronization job started for this batch.
    /// Cached so retries / status polls don't re-resolve the SP every call.
    /// </summary>
    public string? CrossTenantSyncJobId { get; set; }

    /// <summary>
    /// CrossTenantSync only — synchronization rule ID extracted from the job
    /// schema, required as a parameter for <c>provisionOnDemand</c>.
    /// </summary>
    public string? CrossTenantSyncRuleId { get; set; }

    /// <summary>Optional wave this batch belongs to.</summary>
    public Guid? WaveId { get; set; }
    public MigrationWave? Wave { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastUpdatedAt { get; set; }
}
