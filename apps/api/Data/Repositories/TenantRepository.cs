using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITenantRepository"/>.
/// </summary>
public sealed class TenantRepository : ITenantRepository
{
    private readonly AppDbContext _db;

    public TenantRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Tenants.OrderBy(t => t.CreatedAt).ToListAsync(ct);

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Tenants.FindAsync([id], ct);

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        await _db.Tenants.AnyAsync(t => t.Id == id, ct);

    public async Task AddAsync(Tenant tenant, CancellationToken ct = default) =>
        await _db.Tenants.AddAsync(tenant, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is not null)
        {
            _db.Tenants.Remove(tenant);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task UpdateCredentialsAsync(
        Guid id,
        AuthMethod authMethod,
        string? appClientId,
        string? clientSecretHint,
        string? clientCertificateBase64,
        string? clientCertificatePassword,
        string? clientCertificateThumbprint,
        CancellationToken ct = default)
    {
        var setters = _db.Tenants.Where(t => t.Id == id);
        await setters.ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AuthMethod, authMethod)
            .SetProperty(t => t.ClientSecretHint, clientSecretHint)
            .SetProperty(t => t.ClientCertificateBase64, clientCertificateBase64)
            .SetProperty(t => t.ClientCertificatePassword, clientCertificatePassword)
            .SetProperty(t => t.ClientCertificateThumbprint, clientCertificateThumbprint),
            ct);

        if (appClientId is not null)
            await _db.Tenants
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AppClientId, appClientId), ct);
    }

    public async Task UpdateConnectionStatusAsync(
        Guid id,
        ConnectionStatus status,
        DateTime? lastVerifiedAt,
        bool adminConsentGranted,
        CancellationToken ct = default)
    {
        await _db.Tenants
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.ConnectionStatus, status)
                .SetProperty(t => t.LastVerifiedAt, lastVerifiedAt)
                .SetProperty(t => t.AdminConsentGranted, adminConsentGranted),
                ct);
    }

    public async Task UpdateOnMicrosoftDomainAsync(Guid id, string prefix, CancellationToken ct = default)
    {
        await _db.Tenants
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.OnMicrosoftDomain, prefix),
                ct);
    }

    public async Task SaveAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
