using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IDomainRuleRepository"/>.
/// </summary>
public sealed class DomainRuleRepository : IDomainRuleRepository
{
    private readonly AppDbContext _db;

    public DomainRuleRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<DomainRule>> GetByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.DomainRules
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

    public async Task<DomainRule?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.DomainRules.FindAsync([id], ct);

    public async Task AddAsync(DomainRule rule, CancellationToken ct = default) =>
        await _db.DomainRules.AddAsync(rule, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        await _db.DomainRules
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(ct);

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
