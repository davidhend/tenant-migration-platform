using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="AuditEvent"/> entities.
/// </summary>
public interface IAuditRepository
{
    /// <summary>
    /// Returns a page of audit events ordered by timestamp descending, optionally
    /// filtered by project ID.
    /// </summary>
    /// <returns>
    /// A tuple of (Items, TotalCount) where TotalCount reflects the unfiltered/unpaged
    /// total so callers can render pagination controls.
    /// </returns>
    Task<(IEnumerable<AuditEvent> Items, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        Guid? projectId = null,
        CancellationToken ct = default);

    /// <summary>Adds a new audit event to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(AuditEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Deletes up to <paramref name="maxRows"/> audit events with a timestamp
    /// strictly before <paramref name="cutoffUtc"/>. Returns the number deleted so
    /// callers can loop until a sweep drains. Persists immediately (no SaveAsync).
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, int maxRows, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
