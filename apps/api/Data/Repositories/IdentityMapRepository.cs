using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IIdentityMapRepository"/>.
/// </summary>
public sealed class IdentityMapRepository : IIdentityMapRepository
{
    private readonly AppDbContext _db;

    public IdentityMapRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<IdentityMap>> GetByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.IdentityMaps
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.SourceUpn)
            .ToListAsync(ct);

    public async Task<IdentityMap?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.IdentityMaps.FindAsync([id], ct);

    public async Task AddAsync(IdentityMap map, CancellationToken ct = default) =>
        await _db.IdentityMaps.AddAsync(map, ct);

    public async Task AddRangeAsync(IEnumerable<IdentityMap> maps, CancellationToken ct = default) =>
        await _db.IdentityMaps.AddRangeAsync(maps, ct);

    public async Task DeleteAutoMapsForProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.IdentityMaps
            .Where(m => m.ProjectId == projectId && m.MappingSource == MappingSource.Auto)
            .ExecuteDeleteAsync(ct);

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
