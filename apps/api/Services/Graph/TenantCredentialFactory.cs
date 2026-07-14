using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Production implementation of <see cref="ITenantCredentialFactory"/>.
/// Resolves tenant credentials (certificate or client secret) and returns a raw
/// <see cref="TokenCredential"/> that can be used with any Microsoft API scope.
/// </summary>
/// <remarks>
/// Credential resolution priority:
/// <list type="number">
///   <item>Certificate bytes (override or model property) — builds <c>ClientCertificateCredential</c>.</item>
///   <item>Client secret (override or model property) — builds <c>ClientSecretCredential</c>.</item>
/// </list>
/// Throws <see cref="InvalidOperationException"/> when neither is available.
/// Uses the same flags as <see cref="GraphClientFactory"/> for certificate loading.
/// </remarks>
public sealed class TenantCredentialFactory : ITenantCredentialFactory
{
    private readonly ILogger<TenantCredentialFactory> _logger;

    public TenantCredentialFactory(ILogger<TenantCredentialFactory> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public TokenCredential CreateCredential(
        Tenant tenant,
        string? certBase64Override,
        string? certPasswordOverride,
        string? secretOverride)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            throw new InvalidOperationException(
                $"Tenant '{tenant.DisplayName}' ({tenant.Id}) has no TenantId configured.");

        if (string.IsNullOrWhiteSpace(tenant.AppClientId))
            throw new InvalidOperationException(
                $"Tenant '{tenant.DisplayName}' ({tenant.Id}) has no AppClientId configured.");

        // Merge override values with model properties — override wins when non-null.
        var effectiveCertBase64   = certBase64Override   ?? tenant.ClientCertificateBase64;
        var effectiveCertPassword = certPasswordOverride ?? tenant.ClientCertificatePassword;
        var effectiveSecret       = secretOverride       ?? tenant.ClientSecretPlain;

        if (!string.IsNullOrWhiteSpace(effectiveCertBase64))
            return CreateWithCertificate(tenant, effectiveCertBase64, effectiveCertPassword);

        if (!string.IsNullOrWhiteSpace(effectiveSecret))
            return CreateWithSecret(tenant, effectiveSecret);

        throw new InvalidOperationException(
            $"Tenant '{tenant.DisplayName}' ({tenant.Id}) has no credentials configured. " +
            "Set ClientCertificateBase64 (preferred) or ClientSecretPlain before calling external APIs.");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private TokenCredential CreateWithCertificate(
        Tenant tenant,
        string certBase64,
        string? certPassword)
    {
        _logger.LogDebug(
            "Creating TokenCredential for tenant {TenantId} using certificate credential (thumbprint: {Thumbprint})",
            tenant.TenantId,
            tenant.ClientCertificateThumbprint ?? "unknown");

        byte[] certBytes;
        try
        {
            certBytes = Convert.FromBase64String(certBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"The certificate base64 for tenant '{tenant.DisplayName}' ({tenant.Id}) " +
                "is not valid base64.", ex);
        }

        // EphemeralKeySet avoids writing the private key to disk (container-safe).
        // MachineKeySet is added as a fallback for Linux hosts.
        X509Certificate2 certificate;
        try
        {
            certificate = new X509Certificate2(
                certBytes,
                password: certPassword,
                keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.MachineKeySet);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load the certificate for tenant '{tenant.DisplayName}' ({tenant.Id}). " +
                "Ensure the base64 value is a valid PFX with the correct password.", ex);
        }

        return new ClientCertificateCredential(
            tenantId: tenant.TenantId,
            clientId: tenant.AppClientId,
            clientCertificate: certificate);
    }

    private TokenCredential CreateWithSecret(Tenant tenant, string clientSecret)
    {
        _logger.LogDebug(
            "Creating TokenCredential for tenant {TenantId} using client secret credential",
            tenant.TenantId);

        return new ClientSecretCredential(
            tenantId: tenant.TenantId,
            clientId: tenant.AppClientId,
            clientSecret: clientSecret);
    }
}
