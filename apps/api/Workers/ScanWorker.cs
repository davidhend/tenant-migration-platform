using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Discovery;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that dequeues scan job IDs from the in-process
/// <see cref="ScanJobQueue"/> and executes them via <see cref="IDiscoveryEngine"/>.
/// A new DI scope is created per scan so that scoped services (DbContext, repositories)
/// are correctly scoped to the unit of work.
/// </summary>
public class ScanWorker : BackgroundService
{
    private readonly ScanJobQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<ScanWorker> _logger;

    public ScanWorker(ScanJobQueue queue, IServiceProvider services, ILogger<ScanWorker> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanWorker started.");

        // Single-instance safety: only the primary instance drains the scan queue
        // (see SingleInstanceGuard) — a secondary would re-run the same scans.
        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "ScanWorker: not the primary instance — scan processing suppressed.");
            return;
        }

        await RehydrateQueuedScansAsync(stoppingToken);

        try
        {
            await foreach (var scanId in _queue.Channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessScanAsync(scanId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A transient DB failure (or any other escape) must never stop the
                    // host — .NET 8 defaults BackgroundService exceptions to StopHost.
                    _logger.LogError(ex,
                        "ScanWorker: unhandled error processing scan {ScanId} — worker continues.", scanId);
                    _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("ScanWorker");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown cancels ReadAllAsync — exit quietly instead of
            // surfacing as a failed BackgroundService.
        }
    }

    /// <summary>
    /// On startup, re-enqueue any scans left Queued or Running in the database by
    /// a previous process — the in-memory channel does not survive a restart.
    /// Scans are idempotent, so re-running a scan interrupted mid-flight is safe.
    /// </summary>
    private async Task RehydrateQueuedScansAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var scanRepo = scope.ServiceProvider.GetRequiredService<IScanRepository>();
            var scans = await scanRepo.GetAllAsync(null, ct);
            foreach (var scan in scans.Where(s => s.Status is ScanStatus.Queued or ScanStatus.Running))
            {
                _logger.LogInformation(
                    "ScanWorker: re-hydrating scan {ScanId} ({Status}).", scan.Id, scan.Status);
                _queue.Channel.Writer.TryWrite(scan.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ScanWorker: startup re-hydration failed — continuing without it. " +
                "Verify PostgreSQL is reachable at ConnectionStrings:DefaultConnection.");
        }
    }

    private async Task ProcessScanAsync(Guid scanId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanWorker: dequeued scan {ScanId}", scanId);

        // Create a fresh scope for every scan so DbContext is not shared across scans
        using var scope = _services.CreateScope();
        var jobRepo  = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var scanRepo = scope.ServiceProvider.GetRequiredService<IScanRepository>();
        var engine   = scope.ServiceProvider.GetRequiredService<IDiscoveryEngine>();
        var notifier = scope.ServiceProvider.GetRequiredService<IProgressNotifier>();

        Job? job = null;
        try
        {
            job = await jobRepo.GetByScanIdAsync(scanId, stoppingToken);

            // Honour a cancel requested while the scan sat in the queue.
            if (_queue.ConsumePendingCancel(scanId) || job?.Status == JobStatus.Cancelled)
            {
                _logger.LogInformation("ScanWorker: scan {ScanId} cancelled before start — discarding.", scanId);
                await MarkCancelledAsync(jobRepo, scanRepo, notifier, job, scanId, stoppingToken);
                return;
            }

            // Mark the associated job as running
            if (job is not null)
            {
                job.Status    = JobStatus.Running;
                job.StartedAt = DateTime.UtcNow;
                await jobRepo.SaveAsync(stoppingToken);
                await NotifyJobSafe(notifier, job, _logger, stoppingToken);
            }

            // Best-effort: detect the target tenant's onmicrosoft.com prefix so the
            // content migration dialog can auto-fill target SharePoint/OneDrive URLs.
            var scan = await scanRepo.GetByIdAsync(scanId, stoppingToken);
            if (scan?.ProjectId is { } projectId)
                await DetectTargetTenantDomainSafe(scope.ServiceProvider, projectId, _logger, stoppingToken);

            var scanCts = _queue.RegisterRunning(scanId, stoppingToken);
            try
            {
                await engine.RunScanAsync(scanId, scanCts.Token);
            }
            finally
            {
                _queue.UnregisterRunning(scanId);
            }

            if (job is not null)
            {
                // Refresh the scan to pick up summary counts written by DiscoveryEngine
                var refreshedScan = await scanRepo.GetByIdAsync(scanId, stoppingToken);
                job.Status       = JobStatus.Completed;
                job.Progress     = 100;
                job.CompletedAt  = DateTime.UtcNow;
                job.ItemsTotal   = job.ItemsProcessed = refreshedScan?.Summary?.UserCount ?? 0;
                await jobRepo.SaveAsync(stoppingToken);
                await NotifyJobSafe(notifier, job, _logger, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("ScanWorker: scan {ScanId} interrupted by host shutdown.", scanId);
            if (job is not null)
            {
                job.Status = JobStatus.Cancelled;
                // Use CancellationToken.None so the status is persisted even during host shutdown
                await jobRepo.SaveAsync(CancellationToken.None);
                await NotifyJobSafe(notifier, job, _logger, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // User-requested cancel via JobsController → ScanJobQueue.RequestCancel.
            _logger.LogWarning("ScanWorker: scan {ScanId} cancelled by user.", scanId);
            await MarkCancelledAsync(jobRepo, scanRepo, notifier, job, scanId, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanWorker: scan {ScanId} failed.", scanId);
            if (job is not null)
            {
                job.Status       = JobStatus.Failed;
                job.ErrorMessage = ex.Message;
                await jobRepo.SaveAsync(CancellationToken.None);
                await NotifyJobSafe(notifier, job, _logger, CancellationToken.None);
            }
        }
    }

    /// <summary>Persist the Cancelled outcome on both the job and the scan.</summary>
    private async Task MarkCancelledAsync(
        IJobRepository jobRepo,
        IScanRepository scanRepo,
        IProgressNotifier notifier,
        Job? job,
        Guid scanId,
        CancellationToken ct)
    {
        if (job is not null)
        {
            job.Status      = JobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
        }

        // ScanStatus has no Cancelled value — record the outcome as Failed with a
        // clear message so the UI doesn't show a scan stuck in Queued/Running.
        var scan = await scanRepo.GetByIdAsync(scanId, ct);
        if (scan is not null && scan.Status is ScanStatus.Queued or ScanStatus.Running)
        {
            scan.Status       = ScanStatus.Failed;
            scan.ErrorMessage = "Cancelled by user.";
            await scanRepo.SaveAsync(ct);
        }

        if (job is not null)
        {
            await jobRepo.SaveAsync(ct);
            await NotifyJobSafe(notifier, job, _logger, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects the target tenant's onmicrosoft.com domain prefix via the Graph
    /// Domains API and persists it so content migration URLs can be auto-filled.
    /// Skipped when the prefix is already known. Never throws — errors are logged.
    /// </summary>
    private static async Task DetectTargetTenantDomainSafe(
        IServiceProvider services,
        Guid projectId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var projectRepo = services.GetRequiredService<IProjectRepository>();
            var project = await projectRepo.GetByIdWithTenantsAsync(projectId, ct);
            var targetTenant = project?.TargetTenant;
            if (targetTenant is null || !string.IsNullOrWhiteSpace(targetTenant.OnMicrosoftDomain))
                return;

            var keyVault = services.GetRequiredService<IKeyVaultCredentialService>();
            var graphFactory = services.GetRequiredService<IGraphClientFactory>();
            var tenantRepo = services.GetRequiredService<ITenantRepository>();

            var (kvCertBase64, kvCertPassword, kvSecret) = await keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
            var graphClient = graphFactory.CreateForTenant(targetTenant, kvCertBase64, kvCertPassword, kvSecret);

            var domains = await graphClient.Domains.GetAsync(cancellationToken: ct);
            var initial = domains?.Value?
                .FirstOrDefault(d => d.IsInitial == true &&
                                     d.Id != null &&
                                     d.Id.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase));

            if (initial?.Id is null)
            {
                logger.LogWarning(
                    "ScanWorker: could not detect onmicrosoft.com domain for target tenant {TenantId}.",
                    targetTenant.Id);
                return;
            }

            var prefix = initial.Id[..initial.Id.LastIndexOf(".onmicrosoft.com",
                StringComparison.OrdinalIgnoreCase)];

            if (!string.IsNullOrWhiteSpace(prefix))
            {
                await tenantRepo.UpdateOnMicrosoftDomainAsync(targetTenant.Id, prefix, ct);
                logger.LogInformation(
                    "ScanWorker: detected target tenant {TenantId} OnMicrosoftDomain = '{Prefix}'.",
                    targetTenant.Id, prefix);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ScanWorker: failed to detect target tenant OnMicrosoftDomain for project {ProjectId} — skipping.",
                projectId);
        }
    }

    /// <summary>
    /// Fire a <c>JobProgress</c> SignalR event without propagating any failure.
    /// A missing connection or network error must never crash the worker.
    /// </summary>
    private static async Task NotifyJobSafe(
        IProgressNotifier notifier,
        Job job,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await notifier.NotifyJobProgressAsync(
                job.Id,
                job.ProjectId,
                job.Progress,
                job.Status.ToString(),
                job.Type.ToString(),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ScanWorker: SignalR notification failed for job {JobId} — ignoring.", job.Id);
        }
    }
}
