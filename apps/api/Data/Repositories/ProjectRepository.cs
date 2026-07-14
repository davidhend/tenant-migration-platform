using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IProjectRepository"/>.
/// </summary>
public sealed class ProjectRepository : IProjectRepository
{
    private readonly AppDbContext _db;

    public ProjectRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<MigrationProject>> GetAllWithTenantsAsync(CancellationToken ct = default) =>
        await _db.Projects
            .Include(p => p.SourceTenant)
            .Include(p => p.TargetTenant)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<MigrationProject?> GetByIdWithTenantsAsync(Guid id, CancellationToken ct = default) =>
        await _db.Projects
            .Include(p => p.SourceTenant)
            .Include(p => p.TargetTenant)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        await _db.Projects.AnyAsync(p => p.Id == id, ct);

    public async Task AddAsync(MigrationProject project, CancellationToken ct = default) =>
        await _db.Projects.AddAsync(project, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Jobs and Scans have Restrict FKs on ProjectId so they must be removed
        // before the project row.  All other child tables (waves, identity maps,
        // domain rules, mailbox batches, content jobs, validation runs) have
        // Cascade and will be handled by the database automatically.
        // Scans are deleted second because ScannedUser/Group/etc. cascade from Scan.
        await _db.Jobs.Where(j => j.ProjectId == id).ExecuteDeleteAsync(ct);
        await _db.Scans.Where(s => s.ProjectId == id).ExecuteDeleteAsync(ct);
        await _db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
