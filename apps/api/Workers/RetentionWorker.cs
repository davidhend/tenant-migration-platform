using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Services.InstanceLock;
using MigrationPlatform.Api.Services.Telemetry;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Periodically deletes audit events older than the configured retention window.
///
/// SAFETY: destructive, so <c>Retention:Enabled</c> defaults to <b>false</b> —
/// nothing is deleted unless an operator explicitly opts in. Runs only on the
/// primary instance (like the other workers) and only when workers are enabled.
/// Deletions are batched and the loop never throws out of ExecuteAsync.
///
/// v1 scope is audit events only. <c>Retention:CompletedProjectRetentionDays</c>
/// is read but defaults to 0 (never) and project deletion is intentionally not
/// implemented yet — removing a project cascades to migration history, which
/// warrants an explicit, separately-reviewed feature.
/// </summary>
public sealed class RetentionWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private const int DeleteBatchSize = 500;
    private const int MaxBatchesPerSweep = 200; // 100k rows/sweep ceiling — resume next sweep

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IInstanceRole _instanceRole;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IInstanceRole instanceRole,
        ILogger<RetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _instanceRole = instanceRole;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Workers:Enabled", true))
            return;

        if (!SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning("RetentionWorker: not the primary instance — retention suppressed.");
            return;
        }

        if (!_configuration.GetValue("Retention:Enabled", false))
        {
            _logger.LogInformation(
                "RetentionWorker: disabled (Retention:Enabled=false) — no audit-event pruning will occur.");
            return;
        }

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken);

            var intervalHours = Math.Max(1, _configuration.GetValue("Retention:SweepIntervalHours", 24));
            try { await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            var retentionDays = _configuration.GetValue("Retention:AuditEventRetentionDays", 365);
            var cutoff = RetentionPolicy.CutoffUtc(DateTime.UtcNow, retentionDays);

            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();

            var totalDeleted = 0;
            for (var i = 0; i < MaxBatchesPerSweep && !ct.IsCancellationRequested; i++)
            {
                var deleted = await audit.DeleteOlderThanAsync(cutoff, DeleteBatchSize, ct);
                totalDeleted += deleted;
                if (deleted < DeleteBatchSize) break; // drained
            }

            if (totalDeleted > 0)
                _logger.LogInformation(
                    "RetentionWorker: pruned {Count} audit event(s) older than {Cutoff:u} ({Days}d retention).",
                    totalDeleted, cutoff, retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetentionWorker: retention sweep failed — will retry next interval.");
        }
    }
}
