using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IWaveRepository"/>.
/// </summary>
public sealed class WaveRepository : IWaveRepository
{
    private readonly AppDbContext _db;

    public WaveRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<MigrationWave>> GetWavesByProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _db.MigrationWaves
            .Where(w => w.ProjectId == projectId)
            .OrderBy(w => w.Order)
            .ToListAsync(ct);

    public async Task<MigrationWave?> GetWaveByIdAsync(Guid waveId, CancellationToken ct = default) =>
        await _db.MigrationWaves.FindAsync([waveId], ct);

    public async Task<MigrationWave?> GetWaveWithDetailsAsync(Guid waveId, CancellationToken ct = default) =>
        await _db.MigrationWaves
            .Include(w => w.MailboxBatches)
            .Include(w => w.ContentJobs)
            .Include(w => w.UserBatches)
            .AsSplitQuery()
            .FirstOrDefaultAsync(w => w.Id == waveId, ct);

    public async Task AddWaveAsync(MigrationWave wave, CancellationToken ct = default) =>
        await _db.MigrationWaves.AddAsync(wave, ct);

    public async Task<IEnumerable<MigrationWave>> GetScheduledWavesDueAsync(
        DateTime asOf, CancellationToken ct = default) =>
        await _db.MigrationWaves
            .Include(w => w.MailboxBatches)
            .Include(w => w.ContentJobs)
            .Include(w => w.UserBatches)
            .AsSplitQuery()
            .Where(w => w.Status == WaveStatus.Scheduled && w.ScheduledStartAt <= asOf)
            .ToListAsync(ct);

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
