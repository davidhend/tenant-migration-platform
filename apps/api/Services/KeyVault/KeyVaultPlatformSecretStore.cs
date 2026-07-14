using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Key Vault-backed <see cref="IPlatformSecretStore"/>. Uses the same
/// <see cref="DefaultAzureCredential"/> chain as
/// <see cref="KeyVaultCredentialService"/> (env vars → workload identity →
/// managed identity → az login). Reads degrade to null on vault errors so a
/// Key Vault outage never crashes a request path; writes throw so callers
/// never discard plaintext without a confirmed store.
/// </summary>
public sealed class KeyVaultPlatformSecretStore : IPlatformSecretStore
{
    private readonly SecretClient _client;
    private readonly ILogger<KeyVaultPlatformSecretStore> _logger;

    public bool IsExternal => true;

    public KeyVaultPlatformSecretStore(IConfiguration configuration, ILogger<KeyVaultPlatformSecretStore> logger)
    {
        _logger = logger;

        var vaultUri = configuration["KeyVault:VaultUri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
            throw new InvalidOperationException(
                "KeyVaultPlatformSecretStore requires KeyVault:VaultUri. " +
                "Registration should have selected the file-backed store instead.");

        _client = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetSecretAsync(name, cancellationToken: ct);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Key Vault read for platform secret '{Name}' failed — treating as absent.", name);
            return null;
        }
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken ct = default)
    {
        await _client.SetSecretAsync(name, value, ct);
        _logger.LogInformation("Platform secret '{Name}' stored in Key Vault.", name);
    }

    public async Task DeleteSecretAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await _client.StartDeleteSecretAsync(name, ct);
            _logger.LogInformation("Platform secret '{Name}' scheduled for deletion in Key Vault.", name);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Never stored — nothing to delete.
        }
    }
}
