using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAuditRepository"/>.
/// </summary>
public sealed class AuditRepository : IAuditRepository
{
    private readonly AppDbContext _db;

    public AuditRepository(AppDbContext db) => _db = db;

    public async Task<(IEnumerable<AuditEvent> Items, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        Guid? projectId = null,
        CancellationToken ct = default)
    {
        var query = _db.AuditEvents.AsQueryable();
        if (projectId.HasValue)
            query = query.Where(e => e.ProjectId == projectId.Value);

        query = query.OrderByDescending(e => e.Timestamp);

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task AddAsync(AuditEvent evt, CancellationToken ct = default) =>
        await _db.AuditEvents.AddAsync(evt, ct);

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int maxRows, CancellationToken ct = default)
    {
        // Bounded delete via a LIMIT subquery so a huge backlog is drained in
        // batches rather than one giant transaction. ExecuteDelete bypasses the
        // change tracker (no entity load), and parameters are passed safely.
        var batch = Math.Max(1, maxRows);
        return await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""AuditEvents"" WHERE ""Id"" IN (
                   SELECT ""Id"" FROM ""AuditEvents"" WHERE ""Timestamp"" < {cutoffUtc} LIMIT {batch})",
            ct);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
