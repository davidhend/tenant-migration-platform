using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MigrationPlatform.Api.HealthChecks;

/// <summary>
/// Readiness check for Azure Automation configuration presence (SharePoint /
/// OneDrive content migrations run through a runbook there). This is a
/// configuration-completeness check, not a live probe, so it reports
/// <see cref="HealthStatus.Degraded"/> when unset — content migrations will be
/// unavailable but the rest of the platform is fine.
/// </summary>
public sealed class AutomationConfigHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public AutomationConfigHealthCheck(IConfiguration configuration)
        => _configuration = configuration;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var section = _configuration.GetSection("Azure:Automation");
        string[] required = ["SubscriptionId", "ResourceGroup", "AccountName", "RunbookName"];
        var missing = required.Where(k => string.IsNullOrWhiteSpace(section[k])).ToArray();

        return Task.FromResult(missing.Length == 0
            ? HealthCheckResult.Healthy("Azure Automation configured.")
            : HealthCheckResult.Degraded(
                $"Azure Automation not fully configured (missing: {string.Join(", ", missing)}); " +
                "SharePoint/OneDrive content migrations are unavailable."));
    }
}
