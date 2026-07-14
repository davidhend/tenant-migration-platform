using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MigrationPlatform.Api.HealthChecks;

/// <summary>
/// Readiness check for Key Vault reachability. Registered only when Key Vault is
/// enabled. Reports <see cref="HealthStatus.Degraded"/> (never Unhealthy) when
/// the vault is unreachable: platform secrets are cached and the app can keep
/// serving, so a transient vault outage must not fail readiness and cause an
/// orchestrator to kill an otherwise-healthy container.
/// </summary>
public sealed class KeyVaultHealthCheck : IHealthCheck
{
    private readonly string _vaultUri;

    public KeyVaultHealthCheck(IConfiguration configuration)
        => _vaultUri = configuration["KeyVault:VaultUri"] ?? "";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_vaultUri))
            return HealthCheckResult.Degraded("KeyVault:VaultUri not configured.");

        try
        {
            var client = new SecretClient(
                new Uri(_vaultUri),
                new DefaultAzureCredential(),
                new SecretClientOptions { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(5) } });

            // GetPropertiesOfSecrets touches the data plane without needing a
            // specific secret to exist; one page is enough to prove reachability.
            await foreach (var _ in client.GetPropertiesOfSecretsAsync(cancellationToken))
                break;

            return HealthCheckResult.Healthy("Key Vault reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Key Vault unreachable (secrets are cached; serving continues).", ex);
        }
    }
}
