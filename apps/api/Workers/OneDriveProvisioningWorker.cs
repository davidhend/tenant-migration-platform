using System.Collections.Concurrent;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services.Spo;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that monitors OneDrive provisioning for target users of a
/// content-migration job. A job in <see cref="ContentMigrationJobStatus.Provisioning"/>
/// is polled until all target UPNs have a drive (transition to
/// <see cref="ContentMigrationJobStatus.Ready"/>). SPO personal-site provisioning is
/// documented to take up to 24 hours, so a monitoring-session timeout
/// (<c>OneDriveProvisioning:TimeoutMinutes</c>, default 60) does NOT fail the job —
/// it stays in Provisioning and monitoring resumes, up to an overall budget of
/// <c>OneDriveProvisioning:MaxHours</c> (default 24) after which the job is marked
/// Failed. Real provisioning errors mark the job Failed immediately (never Draft —
/// a Draft job hides its error). SignalR progress events are emitted on every
/// status change.
///
/// When <c>Platform:MockGraphCalls</c> is true the worker short-circuits with a
/// small synthetic delay so the UI flow can be exercised without real credentials.
/// </summary>
public class OneDriveProvisioningWorker : BackgroundService
{
    private readonly OneDriveProvisioningQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OneDriveProvisioningWorker> _logger;

    private static readonly ConcurrentDictionary<Guid, byte> _processing = new();

    /// <summary>
    /// When monitoring for a job first began in this process, for the overall
    /// MaxHours budget. Process restarts reset the clock — acceptable, the budget
    /// is a backstop rather than an SLA.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, DateTime> _monitorStartedAt = new();

    /// <summary>
    /// Consecutive Request-SPOPersonalSite Automation-job FAILURES per job (the
    /// runbook job ran and ended non-Completed — distinct from "job succeeded,
    /// site still provisioning"). Resets to 0 when a request succeeds. When it
    /// reaches <c>OneDriveProvisioning:MaxProvisionAttempts</c> the content job is
    /// marked Failed instead of silently resubmitting forever.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, int> _provisionFailures = new();

    private int MaxProvisionAttempts =>
        Math.Max(1, _configuration.GetValue("OneDriveProvisioning:MaxProvisionAttempts", 5));

    private static readonly TimeSpan ProvisionRetryBackoff = TimeSpan.FromSeconds(30);

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Max(5, _configuration.GetValue("OneDriveProvisioning:PollIntervalSeconds", 15)));

    private TimeSpan SessionTimeout =>
        TimeSpan.FromMinutes(Math.Max(1, _configuration.GetValue("OneDriveProvisioning:TimeoutMinutes", 60)));

    private TimeSpan OverallBudget =>
        TimeSpan.FromHours(Math.Max(1, _configuration.GetValue("OneDriveProvisioning:MaxHours", 24)));

    public OneDriveProvisioningWorker(
        OneDriveProvisioningQueue queue,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<OneDriveProvisioningWorker> logger)
    {
        _queue = queue;
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Workers:Enabled", true))
        {
            _logger.LogWarning(
                "OneDriveProvisioningWorker: Workers:Enabled is false — worker is disabled and no OneDrive " +
                "provisioning jobs will progress until it is re-enabled and the API restarts.");
            return;
        }

        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "OneDriveProvisioningWorker: not the primary instance — background processing suppressed.");
            return;
        }

        _logger.LogInformation("OneDriveProvisioningWorker started.");

        // Re-hydrate any jobs left in Provisioning state from a previous process
        // lifetime. The queue is in-memory, so without this any job mid-poll when
        // the API restarted would sit in Provisioning forever.
        await RehydrateActiveJobsAsync(stoppingToken);

        var reader = _queue.Channel.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) { break; }

            while (reader.TryRead(out var jobId))
            {
                // Detach per-job work so a slow poll on one job does not block others.
                _ = Task.Run(() => ProcessAsync(jobId, stoppingToken), stoppingToken);
            }
        }

        _logger.LogInformation("OneDriveProvisioningWorker stopped.");
    }

    /// <summary>
    /// Scan the database for jobs left in <see cref="ContentMigrationJobStatus.Provisioning"/>
    /// and re-enqueue them. Handles the case where the API restarted while the
    /// 10-minute provisioning poll was in flight — without this the job would
    /// sit in Provisioning indefinitely because the queue is process-local.
    /// </summary>
    private async Task RehydrateActiveJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
            var active = await repo.GetActiveJobsAsync(ct);

            foreach (var job in active.Where(j => j.Status == ContentMigrationJobStatus.Provisioning))
            {
                _logger.LogInformation(
                    "OneDriveProvisioningWorker: re-hydrating job {JobId} (Provisioning) from database.",
                    job.Id);
                _queue.Channel.Writer.TryWrite(job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OneDriveProvisioningWorker: error during startup re-hydration.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("OneDriveProvisioningWorker");
        }
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        if (!_processing.TryAdd(jobId, 0))
        {
            _logger.LogDebug("OneDriveProvisioningWorker: job {JobId} already being monitored — skipping.", jobId);
            return;
        }

        try
        {
            await ProcessCoreAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OneDriveProvisioningWorker: unhandled error for job {JobId}.", jobId);
            await TryMarkFailedAsync(jobId, ex.Message, ct);
        }
        finally
        {
            _processing.TryRemove(jobId, out _);
        }
    }

    private async Task ProcessCoreAsync(Guid jobId, CancellationToken ct)
    {
        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");

        if (isMock)
        {
            _logger.LogInformation("OneDriveProvisioningWorker: mock mode — simulating provisioning for job {JobId}.", jobId);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await TransitionToReadyAsync(jobId, ct);
            return;
        }

        // A Graph GET /users/{upn}/drive returns 404 until the personal site has
        // been provisioned, but it does NOT reliably trigger provisioning on its
        // own — SPO only creates the site when something explicitly asks for it
        // (first-party Office sign-in, OneDrive sync client, or the supported
        // Request-SPOPersonalSite cmdlet). Kick that off once upfront so the
        // subsequent polls actually have something to converge on.
        try
        {
            await RequestPersonalSitesAsync(jobId, ct);
            // Request accepted — reset the failure counter; from here it is a
            // legitimate wait for SPO to create the site (minutes–24h).
            _provisionFailures.TryRemove(jobId, out _);
        }
        catch (Exception ex)
        {
            // The Request-SPOPersonalSite runbook job actually FAILED — RunRunbookAsync
            // throws on any non-Completed terminal status. Do NOT silently fall back to
            // polling for a site that was never requested (that hid 19 failures over 18h
            // in live testing). Count it and surface after MaxProvisionAttempts.
            var failures = _provisionFailures.AddOrUpdate(jobId, 1, (_, n) => n + 1);
            _logger.LogWarning(ex,
                "OneDriveProvisioningWorker: Request-SPOPersonalSite job FAILED for content job {JobId} (attempt {Attempt}/{Max}).",
                jobId, failures, MaxProvisionAttempts);

            if (MigrationPlatform.Api.Services.ProvisionRetryPolicy.ShouldFail(failures, MaxProvisionAttempts))
            {
                _monitorStartedAt.TryRemove(jobId, out _);
                _provisionFailures.TryRemove(jobId, out _);
                await TryMarkFailedAsync(jobId,
                    $"OneDrive pre-provisioning failed after {failures} attempt(s): {ex.Message} " +
                    "Note: Request-SPOPersonalSite does not support app-only authentication — a SharePoint admin must run it " +
                    "interactively (Connect-SPOService + Request-SPOPersonalSite; see the project Setup wizard).",
                    ct);
                return;
            }

            // Below the cap — retry the request after a short backoff instead of
            // entering the long poll loop for a site that was never requested.
            ReenqueueAfterDelay(jobId, ProvisionRetryBackoff, ct);
            return;
        }

        var monitorStart = _monitorStartedAt.GetOrAdd(jobId, DateTime.UtcNow);
        var deadline = DateTime.UtcNow + SessionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            bool allReady;
            List<string> pending;
            try
            {
                (allReady, pending) = await CheckAsync(jobId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OneDriveProvisioningWorker: poll error for job {JobId}.", jobId);
                await Task.Delay(PollInterval, ct);
                continue;
            }

            if (allReady)
            {
                _monitorStartedAt.TryRemove(jobId, out _);
                await TransitionToReadyAsync(jobId, ct);
                return;
            }

            _logger.LogDebug(
                "OneDriveProvisioningWorker: job {JobId} — {Count} target user(s) still provisioning.",
                jobId, pending.Count);
            await Task.Delay(PollInterval, ct);
        }

        // Session timeout ≠ failure: SPO personal-site provisioning can take hours.
        // Keep the job in Provisioning and resume monitoring, up to OverallBudget.
        var elapsed = DateTime.UtcNow - monitorStart;
        if (elapsed < OverallBudget)
        {
            _logger.LogWarning(
                "OneDriveProvisioningWorker: job {JobId} still provisioning after {Elapsed:F0} minute(s) — " +
                "continuing to monitor (SPO provisioning can take up to 24h; budget {Budget:F0}h).",
                jobId, elapsed.TotalMinutes, OverallBudget.TotalHours);
            // Delayed re-enqueue for a fresh monitoring session (see ReenqueueAfterDelay).
            ReenqueueAfterDelay(jobId, TimeSpan.FromSeconds(10), ct);
            return;
        }

        _monitorStartedAt.TryRemove(jobId, out _);
        await TryMarkFailedAsync(
            jobId,
            $"OneDrive provisioning did not complete within {OverallBudget.TotalHours:F0} hours. " +
            "Verify the target users have a OneDrive-bearing license and that Request-SPOPersonalSite succeeds, then re-run provisioning.",
            ct);
    }

    private async Task RequestPersonalSitesAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var jobs      = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
        var projects  = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var keyVault  = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
        var spoClient = scope.ServiceProvider.GetRequiredService<ISpoRestClient>();

        var job = await jobs.GetJobByIdAsync(jobId, ct);
        if (job is null) return;

        var project = await projects.GetByIdWithTenantsAsync(job.ProjectId, ct);
        var target = project?.TargetTenant;
        if (target is null || string.IsNullOrWhiteSpace(target.OnMicrosoftDomain))
        {
            _logger.LogWarning(
                "OneDriveProvisioningWorker: target tenant OnMicrosoftDomain missing for job {JobId} — cannot pre-provision.",
                jobId);
            return;
        }

        var items = (await jobs.GetItemsByJobAsync(jobId, ct)).ToList();
        var upns = items
            .Where(i => !string.IsNullOrWhiteSpace(i.TargetOwnerUpn))
            .Select(i => i.TargetOwnerUpn!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (upns.Count == 0)
        {
            _logger.LogDebug("OneDriveProvisioningWorker: no target UPNs on job {JobId} — nothing to pre-provision.", jobId);
            return;
        }

        var (certB64, certPwd) = await keyVault.LoadCertificateWithFallbackAsync(target, ct);
        if (string.IsNullOrEmpty(certB64) ||
            string.IsNullOrWhiteSpace(target.AppClientId) ||
            string.IsNullOrWhiteSpace(target.TenantId))
        {
            _logger.LogWarning(
                "OneDriveProvisioningWorker: target tenant {TenantId} missing app-only certificate, AppClientId, or TenantId — cannot call Request-SPOPersonalSite.",
                target.Id);
            return;
        }

        var creds = new SpoPowerShellCredentials(
            target.TenantId, target.AppClientId, certB64, certPwd);
        var targetAdminUrl = $"https://{target.OnMicrosoftDomain}-admin.sharepoint.com";

        _logger.LogInformation(
            "OneDriveProvisioningWorker: requesting personal sites for {Count} user(s) on {AdminUrl} (job {JobId}).",
            upns.Count, targetAdminUrl, jobId);

        await spoClient.RequestPersonalSiteAsync(targetAdminUrl, upns, creds, ct);
    }

    private async Task<(bool AllReady, List<string> Pending)> CheckAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
        var graphFactory = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();
        var provisioning = scope.ServiceProvider.GetRequiredService<IOneDriveProvisioningService>();

        var job = await jobs.GetJobByIdAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        var project = await projects.GetByIdWithTenantsAsync(job.ProjectId, ct)
            ?? throw new InvalidOperationException($"Project {job.ProjectId} not found.");
        var target = project.TargetTenant
            ?? throw new InvalidOperationException("Target tenant not set on project.");

        var items = (await jobs.GetItemsByJobAsync(jobId, ct)).ToList();
        var upns = items
            .Where(i => !string.IsNullOrWhiteSpace(i.TargetOwnerUpn))
            .Select(i => i.TargetOwnerUpn!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (cert, pw, secret) = await keyVault.LoadCredentialsAsync(target.Id, ct);
        var graph = graphFactory.CreateForTenant(target, cert, pw, secret);
        var results = await provisioning.CheckAndProvisionBatchAsync(graph, upns, ct);

        var pending = results.Where(r => !r.IsProvisioned).Select(r => r.Upn).ToList();
        return (pending.Count == 0, pending);
    }

    private async Task TransitionToReadyAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
        var job = await jobs.GetJobByIdAsync(jobId, ct);
        if (job is null) return;

        // Another action may have already transitioned the job (e.g. cancelled).
        if (job.Status != ContentMigrationJobStatus.Provisioning)
        {
            _logger.LogInformation(
                "OneDriveProvisioningWorker: job {JobId} no longer Provisioning (now {Status}) — not marking Ready.",
                jobId, job.Status);
            return;
        }

        job.Status = ContentMigrationJobStatus.Ready;
        job.ErrorMessage = null;
        job.LastUpdatedAt = DateTime.UtcNow;
        await jobs.SaveAsync(ct);
        _provisionFailures.TryRemove(jobId, out _);

        _logger.LogInformation("OneDriveProvisioningWorker: job {JobId} marked Ready.", jobId);
        await NotifySafe(scope, job, ct);
    }

    /// <summary>
    /// Re-offer a job to the queue after a delay. The delay lets ProcessAsync's
    /// finally release the _processing guard before the id is dequeued again — an
    /// immediate TryWrite could race it and drop the job with nothing to re-offer.
    /// </summary>
    private void ReenqueueAfterDelay(Guid jobId, TimeSpan delay, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                _queue.Channel.Writer.TryWrite(jobId);
            }
            catch (OperationCanceledException)
            {
                // Shutting down — rehydration re-offers Provisioning jobs on restart.
            }
        }, CancellationToken.None);
    }

    private async Task TryMarkFailedAsync(Guid jobId, string error, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
            var job = await jobs.GetJobByIdAsync(jobId, ct);
            if (job is null || job.Status != ContentMigrationJobStatus.Provisioning) return;

            // Failed, NOT Draft: a Draft job looks like it was never started and
            // buries the error. The provision-onedrive endpoint accepts Failed
            // jobs so provisioning can be re-run after the cause is fixed.
            job.Status = ContentMigrationJobStatus.Failed;
            job.ErrorMessage = error;
            job.LastUpdatedAt = DateTime.UtcNow;
            await jobs.SaveAsync(ct);

            _logger.LogWarning("OneDriveProvisioningWorker: job {JobId} provisioning failed — {Error}", jobId, error);
            await NotifySafe(scope, job, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OneDriveProvisioningWorker: failed to mark job {JobId} as failed.", jobId);
        }
    }

    private static async Task NotifySafe(IServiceScope scope, ContentMigrationJob job, CancellationToken ct)
    {
        try
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IProgressNotifier>();
            await notifier.NotifyContentJobProgressAsync(
                job.Id, job.ProjectId,
                job.MigratedItems, job.TotalItems, job.FailedItems,
                job.Status.ToCamelCase(), ct);
        }
        catch
        {
            // SignalR push is best-effort; the status change is already persisted.
        }
    }
}
