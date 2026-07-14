using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="MigrationProject"/> entities.
/// All read methods eagerly load the SourceTenant and TargetTenant navigation properties.
/// </summary>
public interface IProjectRepository
{
    /// <summary>
    /// Returns all projects ordered by creation date descending, with both tenant
    /// navigation properties populated.
    /// </summary>
    Task<IEnumerable<MigrationProject>> GetAllWithTenantsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single project by its primary key, with both tenant navigation properties
    /// populated, or null if not found.
    /// </summary>
    Task<MigrationProject?> GetByIdWithTenantsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns true if a project with the given ID exists; false otherwise.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new project to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(MigrationProject project, CancellationToken ct = default);

    /// <summary>
    /// Deletes a project and all child data in the correct order to satisfy
    /// foreign-key constraints (Jobs and Scans use Restrict; all others cascade).
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
