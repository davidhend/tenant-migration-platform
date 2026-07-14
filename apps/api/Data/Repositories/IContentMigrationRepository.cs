using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="ContentMigrationJob"/> and
/// <see cref="ContentMigrationItem"/> entities.
/// </summary>
public interface IContentMigrationRepository
{
    /// <summary>
    /// Returns all jobs for a given project, ordered by creation date descending.
    /// </summary>
    Task<IEnumerable<ContentMigrationJob>> GetJobsByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns a single job by its primary key, or null if not found.</summary>
    Task<ContentMigrationJob?> GetJobByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single job by its primary key with <see cref="ContentMigrationJob.Project"/>
    /// and <c>Project.SourceTenant</c> navigation properties populated.
    /// Required by the background worker to build tenant credentials without an extra query.
    /// </summary>
    Task<ContentMigrationJob?> GetJobWithProjectAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Returns all items belonging to the specified job.</summary>
    Task<IEnumerable<ContentMigrationItem>> GetItemsByJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Adds a new job to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddJobAsync(ContentMigrationJob job, CancellationToken ct = default);

    /// <summary>Adds a collection of items to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddItemsAsync(IEnumerable<ContentMigrationItem> items, CancellationToken ct = default);

    /// <summary>
    /// Returns all jobs currently in <see cref="ContentMigrationJobStatus.Running"/> state.
    /// Used by the background worker to discover active jobs that need progress polling
    /// after a service restart.
    /// </summary>
    Task<IEnumerable<ContentMigrationJob>> GetActiveJobsAsync(CancellationToken ct = default);

    /// <summary>Removes all items belonging to the specified job from the context.</summary>
    Task RemoveItemsByJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Removes a job and all its items from the context.</summary>
    Task DeleteJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
