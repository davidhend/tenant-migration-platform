using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="IdentityMap"/> entities.
/// </summary>
public interface IIdentityMapRepository
{
    /// <summary>Returns all identity maps for a given project, ordered by source UPN.</summary>
    Task<IEnumerable<IdentityMap>> GetByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns a single identity map by its primary key, or null if not found.</summary>
    Task<IdentityMap?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a single identity map (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(IdentityMap map, CancellationToken ct = default);

    /// <summary>Adds a collection of identity maps in bulk (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddRangeAsync(IEnumerable<IdentityMap> maps, CancellationToken ct = default);

    /// <summary>
    /// Deletes all auto-generated mappings for a project so they can be regenerated
    /// from scratch without affecting manual overrides.
    /// </summary>
    Task DeleteAutoMapsForProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
