using Azure.Core;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Creates a raw <see cref="TokenCredential"/> for a tenant using that tenant's stored
/// app registration credentials. Unlike <see cref="IGraphClientFactory"/> which always
/// targets the Graph scope, this factory returns a generic credential that can be used
/// with any Microsoft API scope (EXO, SPO, etc.) by the caller.
/// </summary>
public interface ITenantCredentialFactory
{
    /// <summary>
    /// Returns a <see cref="TokenCredential"/> for the given tenant using the supplied
    /// credential override values (Key Vault) merged with the tenant model properties.
    /// </summary>
    /// <param name="tenant">Tenant record containing TenantId and AppClientId.</param>
    /// <param name="certBase64Override">Base64-encoded PFX from Key Vault, or null to use tenant model value.</param>
    /// <param name="certPasswordOverride">PFX password from Key Vault, or null to use tenant model value.</param>
    /// <param name="secretOverride">Plain-text secret from Key Vault, or null to use tenant model value.</param>
    /// <returns>A <see cref="TokenCredential"/> suitable for any Azure/Microsoft API scope.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no credentials are available.</exception>
    TokenCredential CreateCredential(
        Tenant tenant,
        string? certBase64Override,
        string? certPasswordOverride,
        string? secretOverride);
}
