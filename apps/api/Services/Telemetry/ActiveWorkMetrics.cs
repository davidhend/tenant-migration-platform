using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.InstanceLock;

namespace MigrationPlatform.Api.Services.Telemetry;

/// <summary>
/// Periodically snapshots active migration work across every workload so the
/// <c>migration.active_batches</c> / <c>migration.stuck_jobs</c> observable
/// gauges and the stuck-jobs health check can read cached counts instead of
/// querying on the metrics collector's thread. Also emits a throttled warning
/// (primary instance only, once per sweep) naming any stuck items — the hook a
/// real alert rule attaches to once an OTel/App Insights backend is configured.
/// </summary>
public sealed class ActiveWorkMetrics : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly PlatformMetrics _metrics;
    private readonly IInstanceRole _instanceRole;
    private readonly ILogger<ActiveWorkMetrics> _logger;

    public ActiveWorkMetrics(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        PlatformMetrics metrics,
        IInstanceRole instanceRole,
        ILogger<ActiveWorkMetrics> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _metrics = metrics;
        _instanceRole = instanceRole;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var thresholdHours = Math.Max(0, _configuration.GetValue("Monitoring:StuckJobThresholdHours", 6));
            var now = DateTime.UtcNow;

            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;

            var counts = new List<KeyValuePair<string, long>>();
            var stuck = new List<string>();

            async Task Tally<T>(
                string kind,
                Task<IEnumerable<T>> query,
                Func<T, DateTime> lastProgress,
                Func<T, bool> isWaitState,
                Func<T, string> describe)
            {
                var items = (await query).ToList();
                counts.Add(new KeyValuePair<string, long>(kind, items.Count));
                foreach (var item in items)
                {
                    if (isWaitState(item)) continue;
                    if (StuckJobPolicy.IsStuck(lastProgress(item), now, thresholdHours))
                        stuck.Add(describe(item));
                }
            }

            var mailbox = sp.GetRequiredService<IMailboxMigrationRepository>();
            var content = sp.GetRequiredService<IContentMigrationRepository>();
            var user = sp.GetRequiredService<IUserMigrationRepository>();
            var validation = sp.GetRequiredService<IValidationRepository>();
            var domain = sp.GetRequiredService<IDomainCutoverRepository>();

            await Tally("mailbox", mailbox.GetActiveBatchesAsync(ct),
                b => b.LastSyncedAt ?? b.StartedAt ?? b.CreatedAt,
                b => b.Status == BatchStatus.Synced, // awaiting cutover — a legit human wait
                b => $"mailbox batch {b.Id} ({b.Name}) status={b.Status}");

            await Tally("content", content.GetActiveJobsAsync(ct),
                j => j.LastUpdatedAt ?? j.StartedAt ?? j.CreatedAt,
                _ => false,
                j => $"content job {j.Id} ({j.Name}) status={j.Status}");

            await Tally("user", user.GetActiveBatchesAsync(ct),
                b => b.LastUpdatedAt ?? b.StartedAt ?? b.CreatedAt,
                _ => false,
                b => $"user batch {b.Id} ({b.Name}) status={b.Status}");

            await Tally("validation", validation.GetActiveRunsAsync(ct),
                r => r.StartedAt ?? r.CreatedAt,
                _ => false,
                r => $"validation run {r.Id} status={r.Status}");

            await Tally("domain", domain.GetActiveJobsAsync(ct),
                j => j.LastUpdatedAt ?? j.StartedAt ?? j.CreatedAt,
                j => j.Phase is DomainCutoverPhase.AwaitingDnsVerification
                              or DomainCutoverPhase.AwaitingMxUpdate, // awaiting admin DNS/MX
                j => $"domain cutover {j.Id} ({j.DomainName}) phase={j.Phase}");

            _metrics.UpdateActiveWork(new ActiveWorkSnapshot(counts, stuck.Count, stuck));

            // Throttled: one warning per sweep, primary only, only when non-empty.
            if (stuck.Count > 0 && _instanceRole.IsPrimary)
                _logger.LogWarning(
                    "Stuck-job detector: {Count} active item(s) past the {Threshold}h threshold — {Items}",
                    stuck.Count, thresholdHours, string.Join("; ", stuck.Take(20)));
        }
        catch (Exception ex)
        {
            // Never let the metrics loop die; keep the last-known snapshot.
            _logger.LogWarning(ex, "ActiveWorkMetrics: refresh sweep failed — retaining previous snapshot.");
        }
    }
}
