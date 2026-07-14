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
/// Background service that drives OneDrive and SharePoint content migration job progress
/// by polling the SPO cross-tenant migration API for each active job.
///
/// Dequeues job IDs written by the controller or wave runner when a job is started or resumed.
/// Re-hydrates any Running jobs from the database on startup (so jobs survive a service restart
/// while the in-process queue was empty).
///
/// Polling strategy: all of a job's SPO sub-move states are fetched with ONE Automation
/// runbook job per cycle (the runbook loops the Get-State cmdlet internally) — an
/// Automation sandbox spin-up costs 1-3 minutes, so per-item polling is prohibitive.
/// Poll pacing is enforced with a per-job next-poll-at gate
/// (<c>Azure:Automation:PollIntervalSeconds</c>, default 120); the 5-second idle sweep
/// re-offers active jobs and the gate skips those not yet due. Jobs are processed
/// concurrently up to <c>ContentMigration:MaxConcurrentJobs</c> (default 3) so a slow
/// job cannot starve the others.
///
/// A new DI scope is created per job so that the scoped <see cref="AppDbContext"/>
/// and repositories are not shared across concurrent job operations.
///
/// Set <c>Workers:Enabled=false</c> to disable all background processing (e.g. for a
/// maintenance window); the default is enabled.
/// </summary>
public class ContentMigrationWorker : BackgroundService
{
    private readonly ContentMigrationQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContentMigrationWorker> _logger;

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<Guid, byte> _processing = new();
    private static readonly ConcurrentDictionary<Guid, int> _consecutiveErrors = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> _nextPollAt = new();

    /// <summary>
    /// Consecutive poll cycles in which SPO reported no state ("NotFound"/"Error")
    /// for an item, keyed "{jobId}:{spoJobId}". SPO lags registering a just-started
    /// move, so a missing state is only fatal after
    /// <c>ContentMigration:NullStateGraceCycles</c> consecutive cycles.
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> _noStateCycles = new();

    private readonly SemaphoreSlim _concurrency;

    public ContentMigrationWorker(
        ContentMigrationQueue queue,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<ContentMigrationWorker> logger)
    {
        _queue = queue;
        _services = services;
        _configuration = configuration;
        _logger = logger;
        _concurrency = new SemaphoreSlim(
            Math.Max(1, configuration.GetValue("ContentMigration:MaxConcurrentJobs", 3)));
    }

    private TimeSpan StatusPollInterval =>
        TimeSpan.FromSeconds(Math.Max(15, _configuration.GetValue("Azure:Automation:PollIntervalSeconds", 120)));

    private int NullStateGraceCycles =>
        Math.Max(1, _configuration.GetValue("ContentMigration:NullStateGraceCycles", 5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Workers:Enabled", true))
        {
            _logger.LogWarning(
                "ContentMigrationWorker: Workers:Enabled is false — worker is disabled and no content " +
                "migration jobs will progress until it is re-enabled and the API restarts.");
            return;
        }

        // Single-instance safety: only the primary instance processes jobs
        // (see SingleInstanceGuard) — a secondary would double-poll and create
        // duplicate SPO migration jobs.
        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "ContentMigrationWorker: not the primary instance — background processing suppressed.");
            return;
        }

        _logger.LogInformation("ContentMigrationWorker started.");

        // Re-hydrate in-flight jobs from the database after a restart.
        await RehydrateActiveJobsAsync(stoppingToken);

        var reader = _queue.Channel.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            bool hasItem;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(IdlePollInterval);
                hasItem = await reader.WaitToReadAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Idle timeout — run the active-job poll sweep then loop back.
                await PollActiveJobsAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                break; // host shutdown — exit quietly instead of failing the service
            }

            if (!hasItem) break; // Channel completed (only happens on shutdown)

            while (reader.TryRead(out var jobId))
            {
                // Cheap in-line gates so the dequeue loop never spawns a task (let
                // alone an Automation job) for work that isn't due or is already running.
                if (_processing.ContainsKey(jobId))
                    continue;
                if (_nextPollAt.TryGetValue(jobId, out var due) && DateTime.UtcNow < due)
                    continue;

                var id = jobId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _concurrency.WaitAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        await ProcessJobAsync(id, stoppingToken);
                    }
                    finally
                    {
                        _concurrency.Release();
                    }
                }, CancellationToken.None);
            }
        }

        _logger.LogInformation("ContentMigrationWorker stopped.");
    }

    // ── Re-hydration on startup ───────────────────────────────────────────────

    /// <summary>
    /// On service startup, find any Running jobs in the database and enqueue them
    /// so they are picked up without waiting for a controller action.
    /// </summary>
    private async Task RehydrateActiveJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
            var active = await repo.GetActiveJobsAsync(ct);

            foreach (var job in active.Where(j => j.Status == ContentMigrationJobStatus.Running))
            {
                _logger.LogInformation(
                    "ContentMigrationWorker: re-hydrating job {JobId} ({Status}) from database.",
                    job.Id, job.Status);
                _queue.Channel.Writer.TryWrite(job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ContentMigrationWorker: error during startup re-hydration.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("ContentMigrationWorker");
        }
    }

    // ── Idle polling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Periodic sweep to re-offer any active jobs; the per-job next-poll-at gate
    /// drops the offers that are not yet due, so this is cheap. Handles the edge
    /// case where the process restarted between a DB write and a queue write, or
    /// where a job was transitioned externally.
    /// </summary>
    private async Task PollActiveJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
            var active = await repo.GetActiveJobsAsync(ct);

            // Provisioning jobs belong to OneDriveProvisioningWorker — polling SPO
            // move state for them would incorrectly fail them ("no SPO job ID").
            foreach (var job in active.Where(j => j.Status == ContentMigrationJobStatus.Running))
                _queue.Channel.Writer.TryWrite(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ContentMigrationWorker: error during idle poll sweep.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("ContentMigrationWorker");
        }
    }

    // ── Job processing ────────────────────────────────────────────────────────

    /// <summary>
    /// Poll SPO once for the job (a single batched runbook call), update the
    /// database, and let the idle sweep re-offer the job for its next cycle.
    /// Never lets an exception escape — an unhandled throw in a background
    /// service stops the whole API host.
    /// </summary>
    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        if (!_processing.TryAdd(jobId, 0))
        {
            _logger.LogDebug("ContentMigrationWorker: job {JobId} already being processed — skipping.", jobId);
            return;
        }

        try
        {
            // Note: _consecutiveErrors is cleared inside the core on a successful
            // SPO poll, NOT here — the core's batch-poll catch increments it and
            // returns normally, and that count must survive the cycle.
            await ProcessJobCoreAsync(jobId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — nothing to record.
        }
        catch (Exception ex)
        {
            await HandleProcessingErrorAsync(jobId, ex, stoppingToken);
        }
        finally
        {
            _processing.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Central handler for unexpected processing errors (DB hiccups, credential
    /// failures, runbook submission errors that escaped the poll path). The job is
    /// only marked Failed after 10 consecutive bad cycles; until then the idle
    /// sweep retries it on the normal poll cadence.
    /// </summary>
    private async Task HandleProcessingErrorAsync(Guid jobId, Exception ex, CancellationToken ct)
    {
        var errorCount = _consecutiveErrors.AddOrUpdate(jobId, 1, (_, c) => c + 1);
        _logger.LogError(ex,
            "ContentMigrationWorker: unhandled error processing job {JobId} (attempt {Count}/10).",
            jobId, errorCount);

        if (errorCount < 10)
            return;

        _consecutiveErrors.TryRemove(jobId, out _);
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
            var job = await repo.GetJobByIdAsync(jobId, ct);
            if (job is null || job.Status != ContentMigrationJobStatus.Running)
                return;

            job.Status = ContentMigrationJobStatus.Failed;
            job.ErrorMessage = $"Content migration processing failed 10 consecutive times. Last error: {ex.Message}";
            job.CompletedAt = DateTime.UtcNow;
            job.LastUpdatedAt = DateTime.UtcNow;
            await repo.SaveAsync(ct);
            await NotifyContentJobSafe(scope, job, _logger, ct);
            CleanupJobTracking(jobId);
        }
        catch (Exception markEx)
        {
            _logger.LogError(markEx,
                "ContentMigrationWorker: failed to mark job {JobId} as Failed after repeated errors.", jobId);
        }
    }

    private static void CleanupJobTracking(Guid jobId)
    {
        _nextPollAt.TryRemove(jobId, out _);
        _consecutiveErrors.TryRemove(jobId, out _);
        foreach (var key in _noStateCycles.Keys.Where(k => k.StartsWith($"{jobId}:", StringComparison.Ordinal)))
            _noStateCycles.TryRemove(key, out _);
    }

    private async Task ProcessJobCoreAsync(Guid jobId, CancellationToken stoppingToken)
    {
        // Poll pacing: skip until the job's next cycle is due. The 5-second idle
        // sweep keeps re-offering active jobs, so no delayed re-enqueue is needed.
        if (_nextPollAt.TryGetValue(jobId, out var due) && DateTime.UtcNow < due)
            return;

        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();

        var job = await repo.GetJobWithProjectAsync(jobId, stoppingToken);
        if (job is null)
        {
            _logger.LogWarning("ContentMigrationWorker: job {JobId} not found — skipping.", jobId);
            CleanupJobTracking(jobId);
            return;
        }

        // Only Running jobs are polled. Provisioning belongs to the provisioning
        // worker; Draft/Ready/Scheduled haven't been submitted; terminal states are done.
        if (job.Status != ContentMigrationJobStatus.Running)
        {
            _logger.LogDebug(
                "ContentMigrationWorker: job {JobId} is in state {Status} — not polling.",
                jobId, job.Status);
            if (job.Status is ContentMigrationJobStatus.Completed or ContentMigrationJobStatus.Failed)
                CleanupJobTracking(jobId);
            return;
        }

        if (string.IsNullOrWhiteSpace(job.SpoMigrationJobId))
        {
            _logger.LogWarning(
                "ContentMigrationWorker: job {JobId} has no SPO migration job ID — marking Failed.",
                jobId);
            job.Status = ContentMigrationJobStatus.Failed;
            job.LastUpdatedAt = DateTime.UtcNow;
            job.ErrorMessage = "Job has no SPO migration job ID — cannot poll status.";
            await repo.SaveAsync(stoppingToken);
            await NotifyContentJobSafe(scope, job, _logger, stoppingToken);
            CleanupJobTracking(jobId);
            return;
        }

        // Mark the start of this poll cycle BEFORE doing the work so error paths
        // are paced too (no hot-looping a failing job through the idle sweep).
        _nextPollAt[jobId] = DateTime.UtcNow + StatusPollInterval;

        // ── Mock mode short-circuit ───────────────────────────────────────────
        // When MockGraphCalls=true or when all SPO job IDs are synthetic (produced
        // by the mock Start path), simulate progress to 100% in a single tick so
        // that the job reaches Completed without any real SPO or credential calls.
        if (_configuration.GetValue<bool>("Platform:MockGraphCalls") ||
            job.SpoMigrationJobId!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .All(id => id.StartsWith("mock-spo-", StringComparison.Ordinal)))
        {
            _logger.LogInformation(
                "ContentMigrationWorker: MockGraphCalls=true — simulating completion for job {JobId}.",
                jobId);
            job.MigratedItems = job.TotalItems;
            job.FailedItems   = 0;
            job.Status        = ContentMigrationJobStatus.Completed;
            job.CompletedAt   = DateTime.UtcNow;
            job.LastUpdatedAt = DateTime.UtcNow;

            // Mark all items as completed in mock mode
            var mockItems = (await repo.GetItemsByJobAsync(jobId, stoppingToken)).ToList();
            foreach (var item in mockItems)
            {
                item.Status = ContentMigrationItemStatus.Completed;
                item.ProgressPercent = 100;
                item.LastUpdated = DateTime.UtcNow;
            }

            await repo.SaveAsync(stoppingToken);
            await NotifyContentJobSafe(scope, job, _logger, stoppingToken);
            CleanupJobTracking(jobId);
            return;
        }

        var sourceTenant = job.Project?.SourceTenant;
        var targetTenant = job.Project?.TargetTenant;
        if (sourceTenant is null || string.IsNullOrWhiteSpace(sourceTenant.OnMicrosoftDomain) ||
            targetTenant is null || string.IsNullOrWhiteSpace(targetTenant.OnMicrosoftDomain))
        {
            _logger.LogWarning(
                "ContentMigrationWorker: job {JobId} missing source/target tenant OnMicrosoftDomain — marking Failed.",
                jobId);
            job.Status = ContentMigrationJobStatus.Failed;
            job.LastUpdatedAt = DateTime.UtcNow;
            job.ErrorMessage = "Source or target tenant OnMicrosoftDomain not found for this job.";
            await repo.SaveAsync(stoppingToken);
            await NotifyContentJobSafe(scope, job, _logger, stoppingToken);
            CleanupJobTracking(jobId);
            return;
        }

        var sourceAdminUrl = $"https://{sourceTenant.OnMicrosoftDomain}-admin.sharepoint.com";
        // The partner (target) cross-tenant host URL — must match what Start used.
        // Get-SPOCrossTenantHostUrl returns the "-my" host for standard tenants,
        // for both user and site moves.
        var targetHostUrl  = $"https://{targetTenant.OnMicrosoftDomain}-my.sharepoint.com";

        var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
        var spoClient = scope.ServiceProvider.GetRequiredService<ISpoRestClient>();

        Services.Spo.SpoPowerShellCredentials spoCreds;
        try
        {
            var (kvCertBase64, kvCertPassword, _) = await keyVault.LoadCredentialsAsync(sourceTenant.Id, stoppingToken);
            if (string.IsNullOrEmpty(kvCertBase64) ||
                string.IsNullOrWhiteSpace(sourceTenant.AppClientId) ||
                string.IsNullOrWhiteSpace(sourceTenant.TenantId))
            {
                throw new InvalidOperationException(
                    "Source tenant is missing an app-only certificate in Key Vault, AppClientId, or TenantId.");
            }
            spoCreds = new Services.Spo.SpoPowerShellCredentials(
                sourceTenant.TenantId, sourceTenant.AppClientId, kvCertBase64, kvCertPassword,
                Services.Spo.SpoPowerShellCredentials.DefaultKeyVaultCertificateName(sourceTenant.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ContentMigrationWorker: failed to build SPO credentials for job {JobId} (tenant {TenantId}) — marking Failed.",
                jobId, sourceTenant.Id);
            job.Status = ContentMigrationJobStatus.Failed;
            job.LastUpdatedAt = DateTime.UtcNow;
            job.ErrorMessage = $"Failed to build source tenant SPO credentials: {ex.Message}";
            await repo.SaveAsync(stoppingToken);
            await NotifyContentJobSafe(scope, job, _logger, stoppingToken);
            CleanupJobTracking(jobId);
            return;
        }

        // Load items for per-item status tracking. Duplicate SPO job IDs (e.g. the
        // same source UPN listed twice) must not crash the poll — group and warn.
        var items = (await repo.GetItemsByJobAsync(jobId, stoppingToken)).ToList();
        var itemsBySpoId = items
            .Where(i => !string.IsNullOrEmpty(i.SpoJobId))
            .GroupBy(i => i.SpoJobId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var dup in itemsBySpoId.Where(kv => kv.Value.Count > 1))
        {
            _logger.LogWarning(
                "ContentMigrationWorker: job {JobId} has {Count} items sharing SPO job ID {SpoJobId} — " +
                "they will all receive the same status.",
                jobId, dup.Value.Count, dup.Key);
        }

        // Parse comma-separated SPO job IDs
        var spoJobIds = job.SpoMigrationJobId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ── ONE batched runbook call for the whole job ────────────────────────
        IReadOnlyList<Services.Spo.SpoMigrationJobStatus> states;
        try
        {
            states = job.JobType == ContentMigrationJobType.SharePoint
                ? await spoClient.GetSiteContentMoveStatesAsync(
                    sourceAdminUrl, targetHostUrl, spoJobIds, spoCreds, stoppingToken)
                : await spoClient.GetUserContentMoveStatesAsync(
                    sourceAdminUrl, targetHostUrl, spoJobIds, spoCreds, stoppingToken);
        }
        catch (Exception ex)
        {
            // Whole-batch poll failure (runbook submission/timeout). Escalate after
            // 10 consecutive failed cycles, otherwise retry on the next cycle.
            var errorCount = _consecutiveErrors.AddOrUpdate(jobId, 1, (_, c) => c + 1);
            if (errorCount >= 10)
            {
                _logger.LogError(ex,
                    "ContentMigrationWorker: job {JobId} has had {Count} consecutive failed poll cycles — marking Failed.",
                    jobId, errorCount);
                job.Status = ContentMigrationJobStatus.Failed;
                job.ErrorMessage = "All SPO status polls failed for 10 consecutive cycles. " +
                    $"Check Azure Automation configuration and credentials. Last error: {ex.Message}";
                job.CompletedAt = DateTime.UtcNow;
                job.LastUpdatedAt = DateTime.UtcNow;
                await repo.SaveAsync(stoppingToken);
                await NotifyContentJobSafe(scope, job, _logger, stoppingToken);
                CleanupJobTracking(jobId);
                return;
            }

            _logger.LogWarning(ex,
                "ContentMigrationWorker: SPO batch poll failed for job {JobId} (attempt {Count}/10) — will retry next cycle.",
                jobId, errorCount);
            return;
        }
        _consecutiveErrors.TryRemove(jobId, out _);

        var stateBySpoId = states
            .GroupBy(s => s.JobId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // ── Aggregate per-item statuses ───────────────────────────────────────
        int completedCount = 0;
        int failedCount = 0;
        int runningCount = 0;

        foreach (var spoJobId in spoJobIds)
        {
            stateBySpoId.TryGetValue(spoJobId, out var status);
            var effectiveStatus = status?.Status ?? "NotFound";
            var graceKey = $"{jobId}:{spoJobId}";

            // SPO can lag registering a just-started move ("NotFound"), and a
            // single Get-State cmdlet error ("Error") is usually transient. Both
            // count as pending until the grace budget is exhausted.
            if (effectiveStatus is "NotFound" or "Error")
            {
                var misses = _noStateCycles.AddOrUpdate(graceKey, 1, (_, c) => c + 1);
                if (misses < NullStateGraceCycles)
                {
                    _logger.LogInformation(
                        "ContentMigrationWorker: SPO job {SpoJobId} state {State} (job {JobId}, cycle {Count}/{Max}) — treating as pending.",
                        spoJobId, effectiveStatus, jobId, misses, NullStateGraceCycles);
                    runningCount++;
                    continue;
                }

                _logger.LogWarning(
                    "ContentMigrationWorker: SPO job {SpoJobId} had no usable state for {Max} consecutive cycles (job {JobId}) — marking Failed.",
                    spoJobId, NullStateGraceCycles, jobId);
                failedCount++;
                if (itemsBySpoId.TryGetValue(spoJobId, out var missingItems))
                {
                    foreach (var missingItem in missingItems)
                    {
                        missingItem.Status = ContentMigrationItemStatus.Failed;
                        missingItem.ErrorMessage = status?.ErrorMessage ??
                            $"SPO reported no state for this move after {NullStateGraceCycles} poll cycles. " +
                            "Verify the move was accepted (Get-SPOCrossTenantUserContentMoveState) and retry.";
                        missingItem.LastUpdated = DateTime.UtcNow;
                    }
                }
                continue;
            }
            _noStateCycles.TryRemove(graceKey, out _);

            _logger.LogDebug(
                "ContentMigrationWorker: SPO job {SpoJobId} status: {Status} ({Progress}%).",
                spoJobId, status!.Status, status.ProgressPercent);

            var itemStatus = MapMoveState(status.Status);

            // Update the corresponding item(s) with per-item details
            if (itemsBySpoId.TryGetValue(spoJobId, out var matched))
            {
                foreach (var item in matched)
                {
                    item.ProgressPercent = status.ProgressPercent;
                    item.LastUpdated = DateTime.UtcNow;
                    item.Status = itemStatus;

                    if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
                        item.ErrorMessage = status.ErrorMessage;

                    switch (status.Status)
                    {
                        case "Rescheduled":
                        case "RescheduleManualTrigger":
                            _logger.LogWarning(
                                "ContentMigrationWorker: SPO sub-job {SpoJobId} is {State} — " +
                                "Microsoft has deferred this move. It may require manual intervention.",
                                spoJobId, status.Status);
                            item.ErrorMessage = "SPO has rescheduled this move. It may complete later or require manual intervention.";
                            break;
                        case "Stopped":
                            item.ErrorMessage ??= "The SPO move was stopped (Stop-SPOCrossTenantUserContentMove or by Microsoft).";
                            break;
                    }
                }
            }

            switch (itemStatus)
            {
                case ContentMigrationItemStatus.Completed:
                    completedCount++;
                    break;
                case ContentMigrationItemStatus.Failed:
                    failedCount++;
                    break;
                default:
                    if (!KnownActiveStates.Contains(status.Status))
                    {
                        _logger.LogWarning(
                            "ContentMigrationWorker: SPO job {SpoJobId} reported unrecognized MoveState '{State}' — treating as running.",
                            spoJobId, status.Status);
                    }
                    runningCount++;
                    break;
            }
        }

        // Update aggregate counts from actual item statuses for accuracy
        job.MigratedItems = items.Count(i => i.Status == ContentMigrationItemStatus.Completed);
        job.FailedItems = items.Count(i => i.Status == ContentMigrationItemStatus.Failed);
        job.LastUpdatedAt = DateTime.UtcNow;

        // Determine aggregate status
        if (failedCount == spoJobIds.Length && spoJobIds.Length > 0)
        {
            job.Status = ContentMigrationJobStatus.Failed;
            job.ErrorMessage = "All SPO migration jobs failed.";
            job.CompletedAt = DateTime.UtcNow;
        }
        else if (completedCount + failedCount == spoJobIds.Length && spoJobIds.Length > 0)
        {
            // All jobs finished — mark completed even if some items failed (like CompletedWithErrors)
            job.Status = ContentMigrationJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            if (failedCount > 0)
                job.ErrorMessage = $"{failedCount} of {spoJobIds.Length} SPO migration job(s) failed.";
        }
        else
        {
            job.Status = ContentMigrationJobStatus.Running;
        }

        // Job-level timeout: fail if running too long
        if (job.Status == ContentMigrationJobStatus.Running && job.StartedAt.HasValue)
        {
            var maxHours = _configuration.GetValue("Platform:ContentMigrationTimeoutHours", 72);
            var maxDuration = TimeSpan.FromHours(maxHours);
            if (DateTime.UtcNow - job.StartedAt.Value > maxDuration)
            {
                _logger.LogWarning(
                    "ContentMigrationWorker: job {JobId} exceeded maximum duration of {Hours}h — marking Failed.",
                    jobId, maxHours);
                job.Status = ContentMigrationJobStatus.Failed;
                job.ErrorMessage = $"Job timed out after {maxHours}h. " +
                    $"SPO status: completed={completedCount}, failed={failedCount}, running={runningCount}.";
                job.CompletedAt = DateTime.UtcNow;
            }
        }

        _logger.LogInformation(
            "ContentMigrationWorker: job {JobId} — SPO aggregate: completed={Completed}, failed={Failed}, running={Running} of {Total} SPO jobs. Local status: {Status}.",
            jobId, completedCount, failedCount, runningCount, spoJobIds.Length, job.Status);

        await repo.SaveAsync(stoppingToken);
        await NotifyContentJobSafe(scope, job, _logger, stoppingToken);

        if (job.Status is ContentMigrationJobStatus.Completed or ContentMigrationJobStatus.Failed)
            CleanupJobTracking(jobId);
        // Still running: the idle sweep re-offers the job and the _nextPollAt gate
        // holds it until the next cycle is due.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SPO MoveState values that legitimately mean "still in flight". Anything
    /// outside this set (and not terminal) is logged and treated as running so a
    /// new Microsoft-side state never wedges or fails a job silently.
    /// </summary>
    internal static readonly HashSet<string> KnownActiveStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "NotStarted", "Scheduled", "ReadyToTrigger", "InProgress",
        "Rescheduled", "Queued", "RescheduleManualTrigger",
    };

    internal static ContentMigrationItemStatus MapMoveState(string moveState) => moveState switch
    {
        "Success"   => ContentMigrationItemStatus.Completed,
        "Completed" => ContentMigrationItemStatus.Completed,
        "Failed"    => ContentMigrationItemStatus.Failed,
        "Stopped"   => ContentMigrationItemStatus.Failed,
        _           => ContentMigrationItemStatus.Running,
    };

    /// <summary>
    /// Push a <c>ContentJobProgress</c> SignalR event without propagating any
    /// failure.  A missing connection or network error must never crash the worker.
    /// </summary>
    private static async Task NotifyContentJobSafe(
        IServiceScope scope,
        ContentMigrationJob job,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IProgressNotifier>();
            await notifier.NotifyContentJobProgressAsync(
                job.Id,
                job.ProjectId,
                job.MigratedItems,
                job.TotalItems,
                job.FailedItems,
                job.Status.ToCamelCase(),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "ContentMigrationWorker: SignalR notification failed for job {JobId} — ignoring.",
                job.Id);
        }
    }
}
