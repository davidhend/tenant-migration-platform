using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="Scan"/> entities and all seven
/// scan sub-collection child tables.
/// </summary>
public interface IScanRepository
{
    // ── Scan root ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all scans, optionally filtered by project ID, ordered by creation
    /// date descending.
    /// </summary>
    Task<IEnumerable<Scan>> GetAllAsync(Guid? projectId = null, CancellationToken ct = default);

    /// <summary>Returns a single scan by its primary key, or null if not found.</summary>
    Task<Scan?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new scan to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(Scan scan, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);

    // ── Sub-collection reads ─────────────────────────────────────────────────

    Task<List<ScannedUser>> GetUsersAsync(Guid scanId, CancellationToken ct = default);
    Task<List<ScannedGroup>> GetGroupsAsync(Guid scanId, CancellationToken ct = default);
    Task<List<ScannedMailbox>> GetMailboxesAsync(Guid scanId, CancellationToken ct = default);
    Task<List<ScannedSite>> GetSitesAsync(Guid scanId, CancellationToken ct = default);
    Task<List<ScannedOneDrive>> GetOneDriveAsync(Guid scanId, CancellationToken ct = default);
    Task<List<ScannedDomain>> GetDomainsAsync(Guid scanId, CancellationToken ct = default);
    Task<List<ScanIssue>> GetIssuesAsync(Guid scanId, CancellationToken ct = default);

    // ── Sub-collection writes (called by DiscoveryEngine) ────────────────────

    /// <summary>
    /// Atomically replaces all users for the given scan: deletes existing rows
    /// then bulk-inserts the new list.
    /// </summary>
    Task ReplaceUsersAsync(Guid scanId, List<ScannedUser> users, CancellationToken ct = default);

    /// <summary>Atomically replaces all groups for the given scan.</summary>
    Task ReplaceGroupsAsync(Guid scanId, List<ScannedGroup> groups, CancellationToken ct = default);

    /// <summary>Atomically replaces all mailboxes for the given scan.</summary>
    Task ReplaceMailboxesAsync(Guid scanId, List<ScannedMailbox> mailboxes, CancellationToken ct = default);

    /// <summary>Atomically replaces all SharePoint sites for the given scan.</summary>
    Task ReplaceSitesAsync(Guid scanId, List<ScannedSite> sites, CancellationToken ct = default);

    /// <summary>Atomically replaces all OneDrive entries for the given scan.</summary>
    Task ReplaceOneDriveAsync(Guid scanId, List<ScannedOneDrive> onedrive, CancellationToken ct = default);

    /// <summary>Atomically replaces all domains for the given scan.</summary>
    Task ReplaceDomainsAsync(Guid scanId, List<ScannedDomain> domains, CancellationToken ct = default);

    /// <summary>Atomically replaces all issues for the given scan.</summary>
    Task ReplaceIssuesAsync(Guid scanId, List<ScanIssue> issues, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recently completed scan for the specified tenant, or null
    /// if no completed scan exists.
    /// </summary>
    Task<Scan?> GetLatestCompletedAsync(Guid tenantId, CancellationToken ct = default);
}
