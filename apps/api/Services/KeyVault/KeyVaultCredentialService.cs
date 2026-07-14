using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Production implementation of <see cref="IKeyVaultCredentialService"/> backed
/// by Azure Key Vault Secrets.
/// </summary>
/// <remarks>
/// Authentication to Key Vault uses <see cref="DefaultAzureCredential"/>, which
/// resolves credentials in this order:
/// <list type="number">
///   <item>Environment variables (<c>AZURE_CLIENT_ID</c> / <c>AZURE_CLIENT_SECRET</c> / <c>AZURE_TENANT_ID</c>)</item>
///   <item>Workload Identity (AKS)</item>
///   <item>Managed Identity (Azure-hosted workloads)</item>
///   <item><c>az login</c> / Visual Studio / VS Code (developer workstations)</item>
/// </list>
///
/// When <c>KeyVault:Enabled</c> is <see langword="false"/> every method is a
/// no-op so the existing database credential columns remain the source of truth.
/// A Key Vault outage degrades gracefully: exceptions are caught, logged as
/// warnings, and the caller falls back to whatever values are in the database.
/// </remarks>
public sealed class KeyVaultCredentialService : IKeyVaultCredentialService
{
    private readonly SecretClient? _client;
    private readonly ILogger<KeyVaultCredentialService> _logger;

    /// <inheritdoc />
    public bool IsEnabled { get; }

    public KeyVaultCredentialService(
        IConfiguration configuration,
        ILogger<KeyVaultCredentialService> logger)
    {
        _logger = logger;

        IsEnabled = configuration.GetValue<bool>("KeyVault:Enabled");

        if (!IsEnabled)
        {
            _logger.LogInformation(
                "Key Vault credential service is disabled (KeyVault:Enabled=false). " +
                "Credentials will be read from the database.");
            return;
        }

        var vaultUri = configuration["KeyVault:VaultUri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
            throw new InvalidOperationException(
                "KeyVault:Enabled is true but KeyVault:VaultUri is not configured. " +
                "Set the vault URI to https://<vault-name>.vault.azure.net/");

        _client = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());

        _logger.LogInformation(
            "Key Vault credential service initialised. Vault: {VaultUri}", vaultUri);
    }

    // ── IKeyVaultCredentialService ────────────────────────────────────────────

    /// <inheritdoc />
    public async Task StoreCredentialsAsync(
        Guid tenantId,
        string? certificateBase64,
        string? certificatePassword,
        string? clientSecret,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug(
                "Key Vault disabled — skipping credential store for tenant {TenantId}.", tenantId);
            return;
        }

        var (certName, certPasswordName, secretName) = SecretNames(tenantId);

        await SetSecretIfNotNullAsync(certName, certificateBase64, "certificate", tenantId, ct);
        await SetSecretIfNotNullAsync(certPasswordName, certificatePassword, "certificate password", tenantId, ct);
        await SetSecretIfNotNullAsync(secretName, clientSecret, "client secret", tenantId, ct);
    }

    /// <inheritdoc />
    public async Task<(string? CertificateBase64, string? CertificatePassword, string? ClientSecret)>
        LoadCredentialsAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return (null, null, null);

        var (certName, certPasswordName, secretName) = SecretNames(tenantId);

        var certBase64      = await GetSecretOrNullAsync(certName, "certificate", tenantId, ct);
        var certPassword    = await GetSecretOrNullAsync(certPasswordName, "certificate password", tenantId, ct);
        var clientSecretVal = await GetSecretOrNullAsync(secretName, "client secret", tenantId, ct);

        return (certBase64, certPassword, clientSecretVal);
    }

    /// <inheritdoc />
    public async Task DeleteCredentialsAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        var (certName, certPasswordName, secretName) = SecretNames(tenantId);

        await DeleteSecretIfExistsAsync(certName, "certificate", tenantId, ct);
        await DeleteSecretIfExistsAsync(certPasswordName, "certificate password", tenantId, ct);
        await DeleteSecretIfExistsAsync(secretName, "client secret", tenantId, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the three Key Vault secret names for a given tenant.
    /// GUIDs are formatted with <c>:N</c> (no hyphens) so the secret name
    /// stays within Key Vault's allowed character set.
    /// </summary>
    private static (string Cert, string CertPassword, string Secret) SecretNames(Guid tenantId)
    {
        var id = tenantId.ToString("N"); // e.g. "550e8400e29b41d4a716446655440000"
        return (
            $"tenant-{id}-cert",
            $"tenant-{id}-cert-password",
            $"tenant-{id}-secret"
        );
    }

    /// <summary>
    /// Sets a Key Vault secret if <paramref name="value"/> is not null or whitespace.
    /// On failure logs a warning and does not rethrow — Key Vault unavailability
    /// must not crash the application.
    /// </summary>
    private async Task SetSecretIfNotNullAsync(
        string secretName,
        string? value,
        string credentialKind,
        Guid tenantId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        try
        {
            await _client!.SetSecretAsync(secretName, value, ct);

            _logger.LogDebug(
                "Stored {CredentialKind} in Key Vault for tenant {TenantId} (secret: {SecretName}).",
                credentialKind, tenantId, secretName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to store {CredentialKind} in Key Vault for tenant {TenantId} " +
                "(secret: {SecretName}). Credential was not persisted to Key Vault.",
                credentialKind, tenantId, secretName);
        }
    }

    /// <summary>
    /// Retrieves a Key Vault secret value, returning <see langword="null"/> for
    /// 404 (not found) and logging a warning for other failures.
    /// </summary>
    private async Task<string?> GetSecretOrNullAsync(
        string secretName,
        string credentialKind,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            var response = await _client!.GetSecretAsync(secretName, version: null, ct);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Secret was never stored — treat as absent, not an error
            _logger.LogDebug(
                "{CredentialKind} secret not found in Key Vault for tenant {TenantId} " +
                "(secret: {SecretName}).",
                credentialKind, tenantId, secretName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load {CredentialKind} from Key Vault for tenant {TenantId} " +
                "(secret: {SecretName}). Falling back to database value.",
                credentialKind, tenantId, secretName);
            return null;
        }
    }

    /// <summary>
    /// Starts a soft-delete of a Key Vault secret, swallowing 404s (secret was
    /// never stored) and logging warnings for other failures.
    /// </summary>
    private async Task DeleteSecretIfExistsAsync(
        string secretName,
        string credentialKind,
        Guid tenantId,
        CancellationToken ct)
    {
        try
        {
            await _client!.StartDeleteSecretAsync(secretName, ct);

            _logger.LogInformation(
                "Scheduled deletion of {CredentialKind} secret in Key Vault for tenant {TenantId} " +
                "(secret: {SecretName}).",
                credentialKind, tenantId, secretName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Secret was never stored — nothing to delete
            _logger.LogDebug(
                "{CredentialKind} secret not present in Key Vault for tenant {TenantId}; " +
                "nothing to delete (secret: {SecretName}).",
                credentialKind, tenantId, secretName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to schedule deletion of {CredentialKind} secret in Key Vault for tenant {TenantId} " +
                "(secret: {SecretName}).",
                credentialKind, tenantId, secretName);
        }
    }
}
