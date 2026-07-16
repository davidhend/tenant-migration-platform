using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Resolves a tenant's app-only certificate with the same precedence the Graph
/// client factory uses: Key Vault when enabled, otherwise the tenant row's
/// database columns (the source of truth when <c>KeyVault:Enabled=false</c>).
/// The SPO cmdlet paths previously consulted only Key Vault and failed on
/// file/database-backed deployments even though the certificate was present.
/// </summary>
public static class TenantCertificateResolver
{
    /// <summary>
    /// Returns the tenant's PFX (base64) and password, preferring Key Vault and
    /// falling back to the tenant record. Both values are null when neither
    /// store has a certificate.
    /// </summary>
    public static async Task<(string? CertificateBase64, string? CertificatePassword)>
        LoadCertificateWithFallbackAsync(
            this IKeyVaultCredentialService keyVault, Tenant tenant, CancellationToken ct = default)
    {
        var (kvCert, kvPassword, _) = await keyVault.LoadCredentialsAsync(tenant.Id, ct);
        if (!string.IsNullOrEmpty(kvCert))
            return (kvCert, kvPassword);

        return (tenant.ClientCertificateBase64, tenant.ClientCertificatePassword);
    }
}
