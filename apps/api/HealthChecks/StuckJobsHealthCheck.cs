using Microsoft.Extensions.Diagnostics.HealthChecks;
using MigrationPlatform.Api.Services.Telemetry;

namespace MigrationPlatform.Api.HealthChecks;

/// <summary>
/// Readiness signal for migration work that has stalled: reports
/// <see cref="HealthStatus.Degraded"/> (never Unhealthy — a stuck job must not
/// 503 the container and pull it out of rotation) when the last active-work
/// snapshot found items past the stuck threshold. Reads the cached snapshot
/// computed by <see cref="ActiveWorkMetrics"/>; no live query here.
/// </summary>
public sealed class StuckJobsHealthCheck : IHealthCheck
{
    private readonly PlatformMetrics _metrics;

    public StuckJobsHealthCheck(PlatformMetrics metrics) => _metrics = metrics;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = _metrics.Current;
        if (snapshot.StuckCount == 0)
            return Task.FromResult(HealthCheckResult.Healthy("No stuck migration jobs."));

        var sample = string.Join("; ", snapshot.StuckDescriptions.Take(10));
        return Task.FromResult(HealthCheckResult.Degraded(
            $"{snapshot.StuckCount} stuck migration item(s) past the configured threshold: {sample}"));
    }
}
