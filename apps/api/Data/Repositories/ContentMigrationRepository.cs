using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IContentMigrationRepository"/>.
/// </summary>
public sealed class ContentMigrationRepository : IContentMigrationRepository
{
    private readonly AppDbContext _db;

    public ContentMigrationRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<ContentMigrationJob>> GetJobsByProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _db.ContentMigrationJobs
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public async Task<ContentMigrationJob?> GetJobByIdAsync(Guid jobId, CancellationToken ct = default) =>
        await _db.ContentMigrationJobs.FindAsync([jobId], ct);

    public async Task<ContentMigrationJob?> GetJobWithProjectAsync(Guid jobId, CancellationToken ct = default) =>
        await _db.ContentMigrationJobs
            .Include(j => j.Project)
                .ThenInclude(p => p!.SourceTenant)
            .Include(j => j.Project)
                .ThenInclude(p => p!.TargetTenant)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

    public async Task<IEnumerable<ContentMigrationItem>> GetItemsByJobAsync(
        Guid jobId, CancellationToken ct = default) =>
        await _db.ContentMigrationItems
            .Where(i => i.JobId == jobId)
            .ToListAsync(ct);

    public async Task AddJobAsync(ContentMigrationJob job, CancellationToken ct = default) =>
        await _db.ContentMigrationJobs.AddAsync(job, ct);

    public async Task AddItemsAsync(IEnumerable<ContentMigrationItem> items, CancellationToken ct = default) =>
        await _db.ContentMigrationItems.AddRangeAsync(items, ct);

    public async Task<IEnumerable<ContentMigrationJob>> GetActiveJobsAsync(CancellationToken ct = default) =>
        await _db.ContentMigrationJobs
            .Where(j => j.Status == ContentMigrationJobStatus.Running ||
                        j.Status == ContentMigrationJobStatus.Provisioning)
            .ToListAsync(ct);

    public async Task RemoveItemsByJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var items = await _db.ContentMigrationItems.Where(i => i.JobId == jobId).ToListAsync(ct);
        _db.ContentMigrationItems.RemoveRange(items);
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var items = await _db.ContentMigrationItems.Where(i => i.JobId == jobId).ToListAsync(ct);
        _db.ContentMigrationItems.RemoveRange(items);
        var job = await _db.ContentMigrationJobs.FindAsync([jobId], ct);
        if (job is not null) _db.ContentMigrationJobs.Remove(job);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
