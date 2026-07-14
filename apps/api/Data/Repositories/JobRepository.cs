using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IJobRepository"/>.
/// </summary>
public sealed class JobRepository : IJobRepository
{
    private readonly AppDbContext _db;

    public JobRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<Job>> GetAllAsync(Guid? projectId = null, CancellationToken ct = default)
    {
        var query = _db.Jobs.AsQueryable();
        if (projectId.HasValue)
            query = query.Where(j => j.ProjectId == projectId.Value);
        return await query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Jobs.FindAsync([id], ct);

    public async Task<Job?> GetByScanIdAsync(Guid scanId, CancellationToken ct = default) =>
        await _db.Jobs.FirstOrDefaultAsync(j => j.ScanId == scanId, ct);

    public async Task AddAsync(Job job, CancellationToken ct = default) =>
        await _db.Jobs.AddAsync(job, ct);

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
