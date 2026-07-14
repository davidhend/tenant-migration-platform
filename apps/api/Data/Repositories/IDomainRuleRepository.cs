using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="DomainRule"/> entities.
/// </summary>
public interface IDomainRuleRepository
{
    /// <summary>
    /// Returns all rules for a project ordered by <see cref="DomainRule.Priority"/> ascending.
    /// </summary>
    Task<IEnumerable<DomainRule>> GetByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns a single rule by its primary key, or null if not found.</summary>
    Task<DomainRule?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new rule to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(DomainRule rule, CancellationToken ct = default);

    /// <summary>Removes a rule from the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
