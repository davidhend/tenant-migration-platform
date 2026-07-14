using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="ValidationRun"/> and
/// <see cref="ValidationCheck"/> entities.
/// </summary>
public interface IValidationRepository
{
    /// <summary>Returns all runs for a project, ordered by creation date descending.</summary>
    Task<IEnumerable<ValidationRun>> GetRunsByProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns a single run by its primary key, or null if not found.</summary>
    Task<ValidationRun?> GetRunByIdAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Returns a run with its <see cref="ValidationRun.Checks"/> collection populated.</summary>
    Task<ValidationRun?> GetRunWithChecksAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Adds a new run to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddRunAsync(ValidationRun run, CancellationToken ct = default);

    /// <summary>Adds a collection of checks to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddChecksAsync(IEnumerable<ValidationCheck> checks, CancellationToken ct = default);

    /// <summary>Returns all runs in Pending or Running state for re-hydration on startup.</summary>
    Task<IEnumerable<ValidationRun>> GetActiveRunsAsync(CancellationToken ct = default);

    /// <summary>Removes a run and (via cascade) its checks. Call <see cref="SaveAsync"/> to persist.</summary>
    Task DeleteRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
