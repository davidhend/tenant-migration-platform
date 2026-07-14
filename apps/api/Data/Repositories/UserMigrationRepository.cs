using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserMigrationRepository"/>.
/// </summary>
public sealed class UserMigrationRepository : IUserMigrationRepository
{
    private readonly AppDbContext _db;

    public UserMigrationRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<UserMigrationBatch>> GetBatchesByProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _db.UserMigrationBatches
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

    public async Task<UserMigrationBatch?> GetBatchByIdAsync(Guid batchId, CancellationToken ct = default) =>
        await _db.UserMigrationBatches.FindAsync([batchId], ct);

    public async Task<UserMigrationBatch?> GetBatchWithProjectAsync(Guid batchId, CancellationToken ct = default) =>
        await _db.UserMigrationBatches
            .Include(b => b.Project)
                .ThenInclude(p => p!.SourceTenant)
            .Include(b => b.Project)
                .ThenInclude(p => p!.TargetTenant)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);

    public async Task<IEnumerable<UserMigrationEntry>> GetEntriesByBatchAsync(
        Guid batchId, CancellationToken ct = default) =>
        await _db.UserMigrationEntries
            .Where(e => e.BatchId == batchId)
            .ToListAsync(ct);

    public async Task AddBatchAsync(UserMigrationBatch batch, CancellationToken ct = default) =>
        await _db.UserMigrationBatches.AddAsync(batch, ct);

    public async Task AddEntriesAsync(IEnumerable<UserMigrationEntry> entries, CancellationToken ct = default) =>
        await _db.UserMigrationEntries.AddRangeAsync(entries, ct);

    public async Task<IEnumerable<UserMigrationBatch>> GetActiveBatchesAsync(CancellationToken ct = default) =>
        await _db.UserMigrationBatches
            .Where(b => b.Status == UserMigrationBatchStatus.Provisioning)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserMigrationEntry>> GetEntriesByBatchAndStatusAsync(
        Guid batchId, UserMigrationEntryStatus status, CancellationToken ct = default) =>
        await _db.UserMigrationEntries
            .Where(e => e.BatchId == batchId && e.Status == status)
            .ToListAsync(ct);

    public async Task DeleteBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var entries = await _db.UserMigrationEntries.Where(e => e.BatchId == batchId).ToListAsync(ct);
        _db.UserMigrationEntries.RemoveRange(entries);
        var batch = await _db.UserMigrationBatches.FindAsync([batchId], ct);
        if (batch is not null) _db.UserMigrationBatches.Remove(batch);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
