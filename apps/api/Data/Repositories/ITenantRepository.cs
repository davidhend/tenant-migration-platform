using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// Persistent storage operations for <see cref="Tenant"/> entities.
/// </summary>
public interface ITenantRepository
{
    /// <summary>Returns all tenants ordered by creation date ascending.</summary>
    Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns a single tenant by its primary key, or null if not found.</summary>
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns true if a tenant with the given primary key exists.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new tenant to the context (call <see cref="SaveAsync"/> to persist).</summary>
    Task AddAsync(Tenant tenant, CancellationToken ct = default);

    /// <summary>Deletes the tenant with the specified primary key. No-ops if not found.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Updates the authentication credentials on a tenant and immediately persists the change.
    /// </summary>
    Task UpdateCredentialsAsync(
        Guid id,
        AuthMethod authMethod,
        string? appClientId,
        string? clientSecretHint,
        string? clientCertificateBase64,
        string? clientCertificatePassword,
        string? clientCertificateThumbprint,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the connection status fields on a tenant and immediately persists the change.
    /// This is a targeted update to avoid clobbering other fields that may have changed concurrently.
    /// </summary>
    Task UpdateConnectionStatusAsync(
        Guid id,
        ConnectionStatus status,
        DateTime? lastVerifiedAt,
        bool adminConsentGranted,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the OnMicrosoftDomain field on a tenant and immediately persists the change.
    /// </summary>
    Task UpdateOnMicrosoftDomainAsync(Guid id, string prefix, CancellationToken ct = default);

    /// <summary>Persists all pending changes tracked by this repository's DbContext.</summary>
    Task SaveAsync(CancellationToken ct = default);
}
