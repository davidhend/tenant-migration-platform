using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

public sealed class DomainCutoverRepository : IDomainCutoverRepository
{
    private readonly AppDbContext _db;
    public DomainCutoverRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<DomainCutoverJob>> GetByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.DomainCutoverJobs
            .Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public async Task<DomainCutoverJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default) =>
        await _db.DomainCutoverJobs.FindAsync([jobId], ct);

    public async Task<DomainCutoverJob?> GetWithProjectAsync(Guid jobId, CancellationToken ct = default) =>
        await _db.DomainCutoverJobs
            .Include(j => j.Project).ThenInclude(p => p!.SourceTenant)
            .Include(j => j.Project).ThenInclude(p => p!.TargetTenant)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

    public async Task<IEnumerable<DomainCutoverJob>> GetActiveJobsAsync(CancellationToken ct = default) =>
        await _db.DomainCutoverJobs
            .Where(j => j.Phase != DomainCutoverPhase.Created
                     && j.Phase != DomainCutoverPhase.AwaitingDnsVerification
                     && j.Phase != DomainCutoverPhase.AwaitingMxUpdate
                     && j.Phase != DomainCutoverPhase.Completed
                     && j.Phase != DomainCutoverPhase.Failed)
            .ToListAsync(ct);

    public async Task AddAsync(DomainCutoverJob job, CancellationToken ct = default) =>
        await _db.DomainCutoverJobs.AddAsync(job, ct);

    public async Task DeleteAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.DomainCutoverJobs.FindAsync([jobId], ct);
        if (job is not null) _db.DomainCutoverJobs.Remove(job);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
