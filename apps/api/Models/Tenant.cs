namespace MigrationPlatform.Api.Models;

public enum TenantRole { Source, Target }
public enum AuthMethod { Certificate, Secret }
public enum ConnectionStatus { Unverified, Connected, Failed, Pending }

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public TenantRole Role { get; set; }
    public string AppClientId { get; set; } = string.Empty;
    public AuthMethod AuthMethod { get; set; }
    public string? ClientSecretHint { get; set; }
    public bool AdminConsentGranted { get; set; }
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Unverified;
    public DateTime? LastVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Base64-encoded PFX/PEM certificate used when <see cref="AuthMethod"/> is
    /// <see cref="AuthMethod.Certificate"/>. Stored encrypted at rest via Key Vault
    /// in production; referenced here as a base64 blob for the dev/staging path.
    /// </summary>
    public string? ClientCertificateBase64 { get; set; }

    /// <summary>
    /// Optional thumbprint of the certificate for logging/auditing purposes.
    /// Not used for credential construction — the actual bytes come from
    /// <see cref="ClientCertificateBase64"/>.
    /// </summary>
    public string? ClientCertificateThumbprint { get; set; }

    /// <summary>
    /// Password for the PFX file stored in <see cref="ClientCertificateBase64"/>.
    /// Stored encrypted at rest via Key Vault in production.
    /// </summary>
    public string? ClientCertificatePassword { get; set; }

    /// <summary>
    /// The tenant's *.onmicrosoft.com domain prefix (e.g., "contoso" from
    /// "contoso.onmicrosoft.com"). Auto-detected during tenant verification by reading
    /// the initial domain from GET /domains. Used to derive the SPO admin URL.
    /// </summary>
    public string? OnMicrosoftDomain { get; set; }

    // Not stored, set at runtime for Graph calls
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ClientSecretPlain { get; set; }
}
