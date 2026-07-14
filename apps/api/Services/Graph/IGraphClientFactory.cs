using Microsoft.Graph;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Creates a configured <see cref="GraphServiceClient"/> scoped to a specific
/// tenant using that tenant's stored app registration credentials.
/// </summary>
/// <remarks>
/// Credential resolution priority (applied to both overloads):
/// <list type="number">
///   <item>
///     <description>
///       Certificate bytes present (from override parameters or
///       <see cref="Tenant.ClientCertificateBase64"/>) → decode PFX/PEM bytes,
///       load as <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>,
///       authenticate via <c>ClientCertificateCredential</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       Client secret present (from override parameters or
///       <see cref="Tenant.ClientSecretPlain"/>) → authenticate via
///       <c>ClientSecretCredential</c>.
///     </description>
///   </item>
/// </list>
/// If neither credential is available an <see cref="InvalidOperationException"/> is thrown.
/// </remarks>
public interface IGraphClientFactory
{
    /// <summary>
    /// Returns a <see cref="GraphServiceClient"/> authenticated as the Azure AD
    /// app registration configured on <paramref name="tenant"/>.
    /// Credentials are read directly from the <paramref name="tenant"/> model
    /// properties (<see cref="Tenant.ClientCertificateBase64"/> /
    /// <see cref="Tenant.ClientSecretPlain"/>).
    /// </summary>
    /// <param name="tenant">
    /// Tenant record containing the Azure AD <c>TenantId</c>, <c>AppClientId</c>,
    /// and at least one of <see cref="Tenant.ClientCertificateBase64"/> or
    /// <see cref="Tenant.ClientSecretPlain"/>.
    /// </param>
    /// <returns>
    /// A fully-configured <see cref="GraphServiceClient"/> targeting
    /// <c>https://graph.microsoft.com/.default</c>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the tenant has neither a certificate nor a client secret configured.
    /// </exception>
    GraphServiceClient CreateForTenant(Tenant tenant);

    /// <summary>
    /// Returns a <see cref="GraphServiceClient"/> using explicitly supplied
    /// credential override values. When an override parameter is non-null it
    /// takes precedence over the corresponding property on <paramref name="tenant"/>.
    /// When all overrides are null, falls back to the tenant model properties —
    /// making this overload safe to call with all-null overrides (Key Vault
    /// disabled) without changing existing behaviour.
    /// </summary>
    /// <param name="tenant">
    /// Tenant record containing the Azure AD <c>TenantId</c> and <c>AppClientId</c>.
    /// </param>
    /// <param name="certBase64Override">
    /// Base64-encoded PFX/PEM bytes loaded from Key Vault, or <see langword="null"/>
    /// to fall back to <see cref="Tenant.ClientCertificateBase64"/>.
    /// </param>
    /// <param name="certPasswordOverride">
    /// PFX password loaded from Key Vault, or <see langword="null"/> to fall back
    /// to <see cref="Tenant.ClientCertificatePassword"/>.
    /// </param>
    /// <param name="secretOverride">
    /// Plain-text client secret loaded from Key Vault, or <see langword="null"/>
    /// to fall back to <see cref="Tenant.ClientSecretPlain"/>.
    /// </param>
    /// <returns>
    /// A fully-configured <see cref="GraphServiceClient"/> targeting
    /// <c>https://graph.microsoft.com/.default</c>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither override nor tenant-model credentials are available.
    /// </exception>
    GraphServiceClient CreateForTenant(
        Tenant tenant,
        string? certBase64Override,
        string? certPasswordOverride,
        string? secretOverride);
}
