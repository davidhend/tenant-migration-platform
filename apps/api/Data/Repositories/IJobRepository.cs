using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="Job"/> entities.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Returns all jobs, optionally filtered by project ID, ordered by creation date
    /// descending.
    /// </summary>
    Task<IEnumerable<Job>> GetAllAsync(Guid? projectId = null, CancellationToken ct = default);

    /// <summary>Returns a single job by its primary key, or null if not found.</summary>
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns the job linked to the specified scan ID, or null if not found.</summary>
    Task<Job?> GetByScanIdAsync(Guid scanId, CancellationToken ct = default);

    /// <summary>Adds a new job to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(Job job, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
