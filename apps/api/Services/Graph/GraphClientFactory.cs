using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Microsoft.Graph;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Production implementation of <see cref="IGraphClientFactory"/>.
/// Resolves tenant credentials from the <see cref="Tenant"/> record (and optional
/// Key Vault override values) then returns a <see cref="GraphServiceClient"/>
/// configured for the <c>https://graph.microsoft.com/.default</c> scope
/// (app-only; delegated consent not required).
/// </summary>
public sealed class GraphClientFactory : IGraphClientFactory
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly ILogger<GraphClientFactory> _logger;

    public GraphClientFactory(ILogger<GraphClientFactory> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public GraphServiceClient CreateForTenant(Tenant tenant)
        => CreateForTenant(tenant, null, null, null);

    /// <inheritdoc />
    public GraphServiceClient CreateForTenant(
        Tenant tenant,
        string? certBase64Override,
        string? certPasswordOverride,
        string? secretOverride)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            throw new InvalidOperationException(
                $"Tenant '{tenant.DisplayName}' ({tenant.Id}) has no TenantId (Azure AD directory ID) configured.");

        if (string.IsNullOrWhiteSpace(tenant.AppClientId))
            throw new InvalidOperationException(
                $"Tenant '{tenant.DisplayName}' ({tenant.Id}) has no AppClientId configured.");

        // Merge override values with tenant model properties.
        // Override wins when non-null; model property is the fallback.
        var effectiveCertBase64  = certBase64Override  ?? tenant.ClientCertificateBase64;
        var effectiveCertPassword = certPasswordOverride ?? tenant.ClientCertificatePassword;
        var effectiveSecret      = secretOverride       ?? tenant.ClientSecretPlain;

        // Certificate takes priority over client secret
        if (!string.IsNullOrWhiteSpace(effectiveCertBase64))
        {
            return CreateWithCertificate(tenant, effectiveCertBase64, effectiveCertPassword);
        }

        if (!string.IsNullOrWhiteSpace(effectiveSecret))
        {
            return CreateWithSecret(tenant, effectiveSecret);
        }

        throw new InvalidOperationException(
            $"Tenant '{tenant.DisplayName}' ({tenant.Id}) has no credentials configured. " +
            "Set ClientCertificateBase64 (preferred) or ClientSecretPlain before calling the Graph API.");
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private GraphServiceClient CreateWithCertificate(
        Tenant tenant,
        string certBase64,
        string? certPassword)
    {
        _logger.LogDebug(
            "Creating Graph client for tenant {TenantId} using certificate credential (thumbprint: {Thumbprint})",
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

        // X509KeyStorageFlags.EphemeralKeySet avoids writing the private key to
        // disk, which is important in container / serverless environments.
        // MachineKeySet is added as a fallback flag for Linux hosts where
        // EphemeralKeySet alone can fail on some .NET builds.
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
                "Ensure the base64 value is a valid PFX with the correct password.",
                ex);
        }

        var credential = new ClientCertificateCredential(
            tenantId: tenant.TenantId,
            clientId: tenant.AppClientId,
            clientCertificate: certificate);

        return new GraphServiceClient(credential, GraphScopes);
    }

    private GraphServiceClient CreateWithSecret(Tenant tenant, string clientSecret)
    {
        _logger.LogDebug(
            "Creating Graph client for tenant {TenantId} using client secret credential",
            tenant.TenantId);

        var credential = new ClientSecretCredential(
            tenantId: tenant.TenantId,
            clientId: tenant.AppClientId,
            clientSecret: clientSecret);

        return new GraphServiceClient(credential, GraphScopes);
    }
}
