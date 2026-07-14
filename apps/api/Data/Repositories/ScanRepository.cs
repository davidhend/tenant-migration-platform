using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IScanRepository"/>.
/// Replace* methods use EF 7+ bulk-delete (<c>ExecuteDeleteAsync</c>) followed by
/// <c>AddRangeAsync</c> to avoid loading child rows into memory.
/// </summary>
public sealed class ScanRepository : IScanRepository
{
    private readonly AppDbContext _db;

    public ScanRepository(AppDbContext db) => _db = db;

    // ── Scan root ────────────────────────────────────────────────────────────

    public async Task<IEnumerable<Scan>> GetAllAsync(Guid? projectId = null, CancellationToken ct = default)
    {
        var query = _db.Scans.AsQueryable();
        if (projectId.HasValue)
            query = query.Where(s => s.ProjectId == projectId.Value);
        return await query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
    }

    public async Task<Scan?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Scans.FindAsync([id], ct);

    public async Task AddAsync(Scan scan, CancellationToken ct = default) =>
        await _db.Scans.AddAsync(scan, ct);

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    // ── Sub-collection reads ─────────────────────────────────────────────────

    public async Task<List<ScannedUser>> GetUsersAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScannedUsers.Where(u => u.ScanId == scanId).ToListAsync(ct);

    public async Task<List<ScannedGroup>> GetGroupsAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScannedGroups.Where(g => g.ScanId == scanId).ToListAsync(ct);

    public async Task<List<ScannedMailbox>> GetMailboxesAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScannedMailboxes.Where(m => m.ScanId == scanId).ToListAsync(ct);

    public async Task<List<ScannedSite>> GetSitesAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScannedSites.Where(s => s.ScanId == scanId).ToListAsync(ct);

    public async Task<List<ScannedOneDrive>> GetOneDriveAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScannedOneDrives.Where(o => o.ScanId == scanId).ToListAsync(ct);

    public async Task<List<ScannedDomain>> GetDomainsAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScannedDomains.Where(d => d.ScanId == scanId).ToListAsync(ct);

    public async Task<List<ScanIssue>> GetIssuesAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.ScanIssues.Where(i => i.ScanId == scanId).ToListAsync(ct);

    // ── Sub-collection writes ────────────────────────────────────────────────

    public async Task ReplaceUsersAsync(Guid scanId, List<ScannedUser> users, CancellationToken ct = default)
    {
        await _db.ScannedUsers.Where(u => u.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (users.Count > 0)
            await _db.ScannedUsers.AddRangeAsync(users, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceGroupsAsync(Guid scanId, List<ScannedGroup> groups, CancellationToken ct = default)
    {
        await _db.ScannedGroups.Where(g => g.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (groups.Count > 0)
            await _db.ScannedGroups.AddRangeAsync(groups, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceMailboxesAsync(Guid scanId, List<ScannedMailbox> mailboxes, CancellationToken ct = default)
    {
        await _db.ScannedMailboxes.Where(m => m.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (mailboxes.Count > 0)
            await _db.ScannedMailboxes.AddRangeAsync(mailboxes, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceSitesAsync(Guid scanId, List<ScannedSite> sites, CancellationToken ct = default)
    {
        await _db.ScannedSites.Where(s => s.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (sites.Count > 0)
            await _db.ScannedSites.AddRangeAsync(sites, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceOneDriveAsync(Guid scanId, List<ScannedOneDrive> onedrive, CancellationToken ct = default)
    {
        await _db.ScannedOneDrives.Where(o => o.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (onedrive.Count > 0)
            await _db.ScannedOneDrives.AddRangeAsync(onedrive, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceDomainsAsync(Guid scanId, List<ScannedDomain> domains, CancellationToken ct = default)
    {
        await _db.ScannedDomains.Where(d => d.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (domains.Count > 0)
            await _db.ScannedDomains.AddRangeAsync(domains, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceIssuesAsync(Guid scanId, List<ScanIssue> issues, CancellationToken ct = default)
    {
        await _db.ScanIssues.Where(i => i.ScanId == scanId).ExecuteDeleteAsync(ct);
        if (issues.Count > 0)
            await _db.ScanIssues.AddRangeAsync(issues, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Scan?> GetLatestCompletedAsync(Guid tenantId, CancellationToken ct = default) =>
        await _db.Scans
            .Where(s => s.TenantId == tenantId && s.Status == ScanStatus.Completed)
            .OrderByDescending(s => s.CompletedAt)
            .FirstOrDefaultAsync(ct);
}
