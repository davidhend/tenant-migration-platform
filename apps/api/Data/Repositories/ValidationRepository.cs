using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>EF Core implementation of <see cref="IValidationRepository"/>.</summary>
public sealed class ValidationRepository : IValidationRepository
{
    private readonly AppDbContext _db;

    public ValidationRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<ValidationRun>> GetRunsByProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _db.ValidationRuns
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public async Task<ValidationRun?> GetRunByIdAsync(Guid runId, CancellationToken ct = default) =>
        await _db.ValidationRuns.FindAsync([runId], ct);

    public async Task<ValidationRun?> GetRunWithChecksAsync(Guid runId, CancellationToken ct = default) =>
        await _db.ValidationRuns
            .Include(r => r.Checks)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

    public async Task AddRunAsync(ValidationRun run, CancellationToken ct = default) =>
        await _db.ValidationRuns.AddAsync(run, ct);

    public async Task AddChecksAsync(IEnumerable<ValidationCheck> checks, CancellationToken ct = default) =>
        await _db.ValidationChecks.AddRangeAsync(checks, ct);

    public async Task<IEnumerable<ValidationRun>> GetActiveRunsAsync(CancellationToken ct = default) =>
        await _db.ValidationRuns
            .Where(r => r.Status == ValidationRunStatus.Pending || r.Status == ValidationRunStatus.Running)
            .ToListAsync(ct);

    public async Task DeleteRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ValidationRuns.FindAsync([runId], ct);
        if (run is not null) _db.ValidationRuns.Remove(run);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
