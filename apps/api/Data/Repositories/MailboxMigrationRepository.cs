using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IMailboxMigrationRepository"/>.
/// </summary>
public sealed class MailboxMigrationRepository : IMailboxMigrationRepository
{
    private readonly AppDbContext _db;

    public MailboxMigrationRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<MailboxMigrationBatch>> GetBatchesByProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _db.MailboxMigrationBatches
            .Where(b => b.ProjectId == projectId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

    public async Task<MailboxMigrationBatch?> GetBatchByIdAsync(Guid batchId, CancellationToken ct = default) =>
        await _db.MailboxMigrationBatches.FindAsync([batchId], ct);

    public async Task<MailboxMigrationBatch?> GetBatchWithProjectAsync(Guid batchId, CancellationToken ct = default) =>
        await _db.MailboxMigrationBatches
            .Include(b => b.Project)
                .ThenInclude(p => p!.SourceTenant)
            .Include(b => b.Project)
                .ThenInclude(p => p!.TargetTenant)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);

    public async Task<IEnumerable<MailboxMigrationEntry>> GetEntriesByBatchAsync(
        Guid batchId, CancellationToken ct = default) =>
        await _db.MailboxMigrationEntries
            .Where(e => e.BatchId == batchId)
            .ToListAsync(ct);

    public async Task<IEnumerable<MailboxMigrationEntry>> GetEntriesByProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _db.MailboxMigrationEntries
            .Where(e => e.Batch!.ProjectId == projectId)
            .ToListAsync(ct);

    public async Task AddBatchAsync(MailboxMigrationBatch batch, CancellationToken ct = default) =>
        await _db.MailboxMigrationBatches.AddAsync(batch, ct);

    public async Task AddEntriesAsync(IEnumerable<MailboxMigrationEntry> entries, CancellationToken ct = default) =>
        await _db.MailboxMigrationEntries.AddRangeAsync(entries, ct);

    // Synced (awaiting cutover) stays active so incremental-sync state and late EXO
    // failures keep flowing to entries while the batch is parked before /complete.
    public async Task<IEnumerable<MailboxMigrationBatch>> GetActiveBatchesAsync(CancellationToken ct = default) =>
        await _db.MailboxMigrationBatches
            .Where(b => b.Status == BatchStatus.Syncing
                     || b.Status == BatchStatus.Synced
                     || b.Status == BatchStatus.Completing)
            .ToListAsync(ct);

    public async Task DeleteBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var entries = await _db.MailboxMigrationEntries.Where(e => e.BatchId == batchId).ToListAsync(ct);
        _db.MailboxMigrationEntries.RemoveRange(entries);
        var batch = await _db.MailboxMigrationBatches.FindAsync([batchId], ct);
        if (batch is not null) _db.MailboxMigrationBatches.Remove(batch);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
