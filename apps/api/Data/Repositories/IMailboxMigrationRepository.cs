using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="MailboxMigrationBatch"/> and
/// <see cref="MailboxMigrationEntry"/> entities.
/// </summary>
public interface IMailboxMigrationRepository
{
    /// <summary>
    /// Returns all batches for a given project, ordered by creation date descending.
    /// </summary>
    Task<IEnumerable<MailboxMigrationBatch>> GetBatchesByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns a single batch by its primary key, or null if not found.</summary>
    Task<MailboxMigrationBatch?> GetBatchByIdAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single batch by its primary key with <see cref="MailboxMigrationBatch.Project"/>
    /// and <c>Project.SourceTenant</c> navigation properties populated.
    /// Required by the background worker to build tenant credentials without an extra query.
    /// </summary>
    Task<MailboxMigrationBatch?> GetBatchWithProjectAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>Returns all entries belonging to the specified batch.</summary>
    Task<IEnumerable<MailboxMigrationEntry>> GetEntriesByBatchAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>
    /// Returns every entry across all of the project's batches. Used for
    /// cross-workload ordering checks (e.g. blocking user migration for users
    /// whose mailbox migration provisions the target identity).
    /// </summary>
    Task<IEnumerable<MailboxMigrationEntry>> GetEntriesByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Adds a new batch to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddBatchAsync(MailboxMigrationBatch batch, CancellationToken ct = default);

    /// <summary>Adds a collection of entries to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddEntriesAsync(IEnumerable<MailboxMigrationEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Returns all batches currently in <see cref="BatchStatus.Syncing"/> or
    /// <see cref="BatchStatus.Completing"/> state. Used by the background worker to
    /// discover active batches that need progress polling.
    /// </summary>
    Task<IEnumerable<MailboxMigrationBatch>> GetActiveBatchesAsync(CancellationToken ct = default);

    /// <summary>Removes a batch and all its entries from the context.</summary>
    Task DeleteBatchAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
