namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Storage for PLATFORM-level secrets (as opposed to per-tenant credentials,
/// which go through <see cref="IKeyVaultCredentialService"/>): the ARM service
/// principal's client secret / PFX, and the cross-tenant Mailbox Migration
/// app's client secret.
///
/// Two implementations: Key Vault (production; secrets never touch disk) and
/// file-backed (dev fallback — writes into <c>settings.override.json</c>,
/// preserving the pre-Key-Vault behavior). Selection is by
/// <c>KeyVault:Enabled</c> + <c>KeyVault:VaultUri</c> at startup.
/// </summary>
public interface IPlatformSecretStore
{
    /// <summary>
    /// True when secrets live outside the config file (Key Vault). Writers use
    /// this to decide whether to place a <c>kv:</c> marker or the actual value
    /// into <c>settings.override.json</c>.
    /// </summary>
    bool IsExternal { get; }

    /// <summary>Fetch a secret by its store name (see <see cref="PlatformSecretNames"/>); null when absent or the store is unavailable.</summary>
    Task<string?> GetSecretAsync(string name, CancellationToken ct = default);

    /// <summary>Persist a secret. Throws on failure — callers must not report success (or discard plaintext) unless this returns.</summary>
    Task SetSecretAsync(string name, string value, CancellationToken ct = default);

    /// <summary>Remove a secret; absent secrets are ignored.</summary>
    Task DeleteSecretAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// Maps config paths of secret-bearing settings to their platform secret-store
/// names (Key Vault naming rules: alphanumeric + hyphens). The
/// <c>kv:{name}</c> marker written into <c>settings.override.json</c> uses the
/// same names.
/// </summary>
public static class PlatformSecretNames
{
    public const string Marker = "kv:";

    /// <summary>Config path → store name for every platform secret the settings surface manages.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByConfigPath =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Platform:CrossTenantMigration:ClientSecret"] = "platform-cross-tenant-migration-client-secret",
            ["Azure:Identity:ClientSecret"]                = "platform-azure-identity-client-secret",
            ["Azure:Identity:CertificateBase64"]           = "platform-azure-identity-certificate",
            ["Azure:Identity:CertificatePassword"]         = "platform-azure-identity-certificate-password",
        };

    /// <summary>Store name for a config path; throws for unknown paths (they must be added to the table deliberately).</summary>
    public static string ForConfigPath(string configPath) =>
        ByConfigPath.TryGetValue(configPath, out var name)
            ? name
            : throw new ArgumentException($"'{configPath}' is not a managed platform secret path.", nameof(configPath));

    /// <summary>True when a config value is a redirect marker rather than a real secret.</summary>
    public static bool IsMarker(string? value) =>
        value is not null && value.StartsWith(Marker, StringComparison.OrdinalIgnoreCase);
}
