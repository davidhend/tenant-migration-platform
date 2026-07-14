using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="MigrationWave"/> entities.
/// </summary>
public interface IWaveRepository
{
    /// <summary>Returns all waves for a given project, ordered by <see cref="MigrationWave.Order"/>.</summary>
    Task<IEnumerable<MigrationWave>> GetWavesByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns a single wave by its primary key, or null if not found.</summary>
    Task<MigrationWave?> GetWaveByIdAsync(Guid waveId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single wave with its <see cref="MigrationWave.MailboxBatches"/> and
    /// <see cref="MigrationWave.ContentJobs"/> navigation collections populated.
    /// </summary>
    Task<MigrationWave?> GetWaveWithDetailsAsync(Guid waveId, CancellationToken ct = default);

    /// <summary>Adds a new wave to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddWaveAsync(MigrationWave wave, CancellationToken ct = default);

    /// <summary>
    /// Returns all waves with status <see cref="WaveStatus.Scheduled"/> whose
    /// <see cref="MigrationWave.ScheduledStartAt"/> is at or before <paramref name="asOf"/>.
    /// Used by <see cref="Workers.WaveSchedulerService"/> to auto-start due waves.
    /// </summary>
    Task<IEnumerable<MigrationWave>> GetScheduledWavesDueAsync(DateTime asOf, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
