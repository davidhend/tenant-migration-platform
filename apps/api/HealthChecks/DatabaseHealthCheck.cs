using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MigrationPlatform.Api.Data;

namespace MigrationPlatform.Api.HealthChecks;

/// <summary>
/// Readiness check for PostgreSQL: opens the application's connection and runs a
/// trivial <c>SELECT 1</c>. Unhealthy when the database cannot be reached, since
/// every controller and worker depends on it.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseHealthCheck(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // ExecuteScalar with a short command timeout via the cancellation token.
            var canConnect = await db.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
                return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");

            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL check failed.", ex);
        }
    }
}
