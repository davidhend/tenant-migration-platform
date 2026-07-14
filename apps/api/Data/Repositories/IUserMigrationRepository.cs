using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="UserMigrationBatch"/> and
/// <see cref="UserMigrationEntry"/> entities.
/// </summary>
public interface IUserMigrationRepository
{
    Task<IEnumerable<UserMigrationBatch>> GetBatchesByProjectAsync(Guid projectId, CancellationToken ct = default);

    Task<UserMigrationBatch?> GetBatchByIdAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single batch with <see cref="UserMigrationBatch.Project"/> and the
    /// target tenant navigation properties populated — the worker needs the target
    /// tenant credentials without a second query.
    /// </summary>
    Task<UserMigrationBatch?> GetBatchWithProjectAsync(Guid batchId, CancellationToken ct = default);

    Task<IEnumerable<UserMigrationEntry>> GetEntriesByBatchAsync(Guid batchId, CancellationToken ct = default);

    Task AddBatchAsync(UserMigrationBatch batch, CancellationToken ct = default);

    Task AddEntriesAsync(IEnumerable<UserMigrationEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Returns all batches currently in <see cref="UserMigrationBatchStatus.Provisioning"/>
    /// state. Used by the background worker to discover active batches on restart.
    /// </summary>
    Task<IEnumerable<UserMigrationBatch>> GetActiveBatchesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UserMigrationEntry>> GetEntriesByBatchAndStatusAsync(
        Guid batchId, UserMigrationEntryStatus status, CancellationToken ct = default);

    Task DeleteBatchAsync(Guid batchId, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}
