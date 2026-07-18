using Microsoft.Graph.Models.ODataErrors;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that drives post-migration validation runs.
///
/// For each user sync entry: calls GET /users/{targetUpn} to verify the user exists.
///
/// For each mailbox entry: calls GET /users/{targetUpn} to verify the migrated mailbox user.
///
/// For each content item:
///   - SharePoint: GET /sites?$filter=webUrl eq '{targetUrl}' — Pass if results non-empty
///   - OneDrive:   GET /users/{ownerUpn}/drive — Pass if 200
///
/// Dequeues run IDs written by the controller when a run is created.
/// Re-hydrates any Pending/Running runs from the database on startup.
///
/// A new DI scope is created per run so that the scoped <see cref="AppDbContext"/>
/// and repositories are not shared across concurrent operations.
///
/// The worker runs by default; set <c>Workers:Enabled=false</c> to disable all
/// background workers (e.g. on an API instance that should only serve HTTP).
/// </summary>
public sealed class ValidationWorker : BackgroundService
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(10);
    private const int CheckFlushBatchSize = 20;

    private readonly ValidationQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ValidationWorker> _logger;

    public ValidationWorker(
        ValidationQueue queue,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<ValidationWorker> logger)
    {
        _queue    = queue;
        _services = services;
        _configuration = configuration;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Workers:Enabled", true))
        {
            _logger.LogWarning(
                "ValidationWorker: disabled via Workers:Enabled=false — queued runs will not execute.");
            return;
        }

        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "ValidationWorker: not the primary instance — background processing suppressed.");
            return;
        }

        _logger.LogInformation("ValidationWorker started.");
        await RehydrateActiveRunsAsync(stoppingToken);

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
                await PollActiveRunsAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                break; // host shutdown — exit quietly instead of failing the service
            }

            if (!hasItem) break;

            while (reader.TryRead(out var runId))
            {
                _logger.LogInformation("ValidationWorker: dequeued run {RunId}.", runId);
                await ProcessRunAsync(runId, stoppingToken);
            }
        }

        _logger.LogInformation("ValidationWorker stopped.");
    }

    // ── Re-hydration ──────────────────────────────────────────────────────────

    private async Task RehydrateActiveRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IValidationRepository>();
            foreach (var run in await repo.GetActiveRunsAsync(ct))
                _queue.Channel.Writer.TryWrite(run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidationWorker: error during startup re-hydration.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("ValidationWorker");
        }
    }

    private async Task PollActiveRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IValidationRepository>();
            foreach (var run in await repo.GetActiveRunsAsync(ct))
                _queue.Channel.Writer.TryWrite(run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidationWorker: error during idle poll sweep.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("ValidationWorker");
        }
    }

    // ── Run processing ────────────────────────────────────────────────────────

    private async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var validationRepo = scope.ServiceProvider.GetRequiredService<IValidationRepository>();
        var projectRepo    = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var keyVault       = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
        var graphFactory   = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();
        var credFactory    = scope.ServiceProvider.GetRequiredService<ITenantCredentialFactory>();
        var batchRepo      = scope.ServiceProvider.GetRequiredService<IMailboxMigrationRepository>();
        var contentRepo    = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
        var userSyncRepo   = scope.ServiceProvider.GetRequiredService<IUserMigrationRepository>();

        var run = await validationRepo.GetRunByIdAsync(runId, ct);
        if (run is null) return;

        // Load project with both tenants
        var project = await projectRepo.GetByIdWithTenantsAsync(run.ProjectId, ct);
        if (project?.TargetTenant is null)
        {
            run.Status       = ValidationRunStatus.Failed;
            run.ErrorMessage = "Target tenant not found for this project.";
            run.CompletedAt  = DateTime.UtcNow;
            await validationRepo.SaveAsync(ct);
            return;
        }

        var targetTenant = project.TargetTenant;

        // Build Graph client for the target tenant
        Microsoft.Graph.GraphServiceClient graphClient;
        try
        {
            var (kvCertBase64, kvCertPassword, kvSecret) = await keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
            graphClient = graphFactory.CreateForTenant(targetTenant, kvCertBase64, kvCertPassword, kvSecret);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex,
                "ValidationWorker: failed to build Graph client for target tenant {TenantId}.",
                targetTenant.Id);
            run.Status       = ValidationRunStatus.Failed;
            run.ErrorMessage = $"Target tenant credentials not available: {ex.Message}";
            run.CompletedAt  = DateTime.UtcNow;
            await validationRepo.SaveAsync(ct);
            return;
        }

        run.Status    = ValidationRunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await validationRepo.SaveAsync(ct);

        // Collect checks to flush
        var pendingChecks = new List<ValidationCheck>();
        int passedCount = 0, failedCount = 0, warningCount = 0;

        // ── Mailbox checks ────────────────────────────────────────────────────

        // Load completed mailbox entries: filter by wave if WaveId is set
        var allBatches = await batchRepo.GetBatchesByProjectAsync(run.ProjectId, ct);
        var relevantBatches = run.WaveId.HasValue
            ? allBatches.Where(b => b.WaveId == run.WaveId && b.Status == BatchStatus.Completed)
            : allBatches.Where(b => b.Status == BatchStatus.Completed);

        var mailboxEntries = new List<MailboxMigrationEntry>();
        foreach (var batch in relevantBatches)
        {
            var entries = await batchRepo.GetEntriesByBatchAsync(batch.Id, ct);
            // Include Synced and Failed entries — SyncedWithErrors marks entries as Failed
            // at the EXO level even though the migration actually completed.
            mailboxEntries.AddRange(entries.Where(e =>
                e.Status is MailboxMigrationStatus.Synced or MailboxMigrationStatus.Failed));
        }

        _logger.LogInformation(
            "ValidationWorker: run {RunId} — validating {Count} mailbox entries.",
            runId, mailboxEntries.Count);

        foreach (var entry in mailboxEntries)
        {
            var check = await CheckMailboxAsync(graphClient, entry, ct);
            pendingChecks.Add(check);

            switch (check.Outcome)
            {
                case ValidationOutcome.Pass:    passedCount++;  break;
                case ValidationOutcome.Fail:    failedCount++;  break;
                case ValidationOutcome.Warning: warningCount++; break;
            }

            if (pendingChecks.Count >= CheckFlushBatchSize)
                await FlushChecksAsync(validationRepo, run, pendingChecks, passedCount, failedCount, warningCount, scope, ct);
        }

        // ── Hybrid directory-link checks (hybrid target projects only) ────────
        // A hybrid target expects every migrated identity to end up mastered by
        // on-prem AD (Entra Connect). Until the AD handoff kit has been run and
        // a sync cycle completed, the platform-created users stay cloud-only —
        // surfaced here as warnings so the handoff cannot be silently forgotten.
        if (project.TargetDirectoryMode == TargetDirectoryMode.Hybrid)
        {
            var syncedEntries = mailboxEntries
                .Where(e => e.Status == MailboxMigrationStatus.Synced && !string.IsNullOrWhiteSpace(e.TargetUpn))
                .ToList();

            _logger.LogInformation(
                "ValidationWorker: run {RunId} — hybrid mode: checking directory-sync linkage for {Count} user(s).",
                runId, syncedEntries.Count);

            foreach (var entry in syncedEntries)
            {
                var check = await CheckDirectoryLinkAsync(graphClient, entry, ct);
                pendingChecks.Add(check);

                switch (check.Outcome)
                {
                    case ValidationOutcome.Pass:    passedCount++;  break;
                    case ValidationOutcome.Fail:    failedCount++;  break;
                    case ValidationOutcome.Warning: warningCount++; break;
                }

                if (pendingChecks.Count >= CheckFlushBatchSize)
                    await FlushChecksAsync(validationRepo, run, pendingChecks, passedCount, failedCount, warningCount, scope, ct);
            }
        }

        // ── User migration checks ─────────────────────────────────────────────

        var allUserBatches = await userSyncRepo.GetBatchesByProjectAsync(run.ProjectId, ct);
        var relevantUserBatches = run.WaveId.HasValue
            ? allUserBatches.Where(b => b.WaveId == run.WaveId && b.Status == UserMigrationBatchStatus.Completed)
            : allUserBatches.Where(b => b.Status == UserMigrationBatchStatus.Completed);

        var userEntries = new List<UserMigrationEntry>();
        foreach (var batch in relevantUserBatches)
        {
            var entries = await userSyncRepo.GetEntriesByBatchAsync(batch.Id, ct);
            // Include Provisioned and Failed so failures surface in the validation run.
            userEntries.AddRange(entries.Where(e =>
                e.Status is UserMigrationEntryStatus.Provisioned or UserMigrationEntryStatus.Failed));
        }

        _logger.LogInformation(
            "ValidationWorker: run {RunId} — validating {Count} user migration entries.",
            runId, userEntries.Count);

        foreach (var entry in userEntries)
        {
            var check = await CheckUserAsync(graphClient, entry, ct);
            pendingChecks.Add(check);

            switch (check.Outcome)
            {
                case ValidationOutcome.Pass:    passedCount++;  break;
                case ValidationOutcome.Fail:    failedCount++;  break;
                case ValidationOutcome.Warning: warningCount++; break;
            }

            if (pendingChecks.Count >= CheckFlushBatchSize)
                await FlushChecksAsync(validationRepo, run, pendingChecks, passedCount, failedCount, warningCount, scope, ct);
        }

        // ── Content checks ────────────────────────────────────────────────────

        var allJobs = await contentRepo.GetJobsByProjectAsync(run.ProjectId, ct);
        var relevantJobs = run.WaveId.HasValue
            ? allJobs.Where(j => j.WaveId == run.WaveId && j.Status == ContentMigrationJobStatus.Completed)
            : allJobs.Where(j => j.Status == ContentMigrationJobStatus.Completed);

        var contentItems = new List<ContentMigrationItem>();
        foreach (var job in relevantJobs)
        {
            var items = await contentRepo.GetItemsByJobAsync(job.Id, ct);
            contentItems.AddRange(items.Where(i => i.Status == ContentMigrationItemStatus.Completed));
        }

        _logger.LogInformation(
            "ValidationWorker: run {RunId} — validating {Count} content items.",
            runId, contentItems.Count);

        foreach (var item in contentItems)
        {
            var check = await CheckContentItemAsync(graphClient, item, ct);
            pendingChecks.Add(check);

            switch (check.Outcome)
            {
                case ValidationOutcome.Pass:    passedCount++;  break;
                case ValidationOutcome.Fail:    failedCount++;  break;
                case ValidationOutcome.Warning: warningCount++; break;
            }

            if (pendingChecks.Count >= CheckFlushBatchSize)
                await FlushChecksAsync(validationRepo, run, pendingChecks, passedCount, failedCount, warningCount, scope, ct);
        }

        // Final flush of any remaining checks
        if (pendingChecks.Count > 0)
            await FlushChecksAsync(validationRepo, run, pendingChecks, passedCount, failedCount, warningCount, scope, ct);

        // Mark run completed
        run.Status       = ValidationRunStatus.Completed;
        run.CompletedAt  = DateTime.UtcNow;
        run.TotalChecks  = userEntries.Count + mailboxEntries.Count + contentItems.Count;
        run.PassedChecks = passedCount;
        run.FailedChecks = failedCount;
        run.WarningChecks = warningCount;
        await validationRepo.SaveAsync(ct);

        // Final SignalR broadcast
        await NotifyValidationSafe(scope, run, _logger, ct);

        _logger.LogInformation(
            "ValidationWorker: run {RunId} completed — {Passed} passed, {Failed} failed, {Warnings} warnings.",
            runId, passedCount, failedCount, warningCount);
    }

    // ── Check helpers ─────────────────────────────────────────────────────────

    private async Task<ValidationCheck> CheckMailboxAsync(
        Microsoft.Graph.GraphServiceClient graphClient,
        MailboxMigrationEntry entry,
        CancellationToken ct)
    {
        var check = new ValidationCheck
        {
            RunId           = Guid.Empty, // set by FlushChecksAsync via run.Id injection
            CheckType       = ValidationCheckType.Mailbox,
            SourceReference = entry.SourceUpn,
            TargetReference = entry.TargetUpn,
            CheckedAt       = DateTime.UtcNow,
        };

        try
        {
            await graphClient.Users[entry.TargetUpn].GetAsync(cancellationToken: ct);
            check.Outcome = ValidationOutcome.Pass;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            check.Outcome      = ValidationOutcome.Fail;
            check.ErrorMessage = $"User '{entry.TargetUpn}' not found in target tenant (404).";
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Access denied checking user '{entry.TargetUpn}' ({ex.ResponseStatusCode}). " +
                                 "Ensure the app has User.Read.All permission in the target tenant.";
        }
        catch (Exception ex)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Error checking user '{entry.TargetUpn}': {ex.Message}";
            _logger.LogWarning(ex, "ValidationWorker: error checking mailbox {TargetUpn}.", entry.TargetUpn);
        }

        return check;
    }

    private async Task<ValidationCheck> CheckUserAsync(
        Microsoft.Graph.GraphServiceClient graphClient,
        UserMigrationEntry entry,
        CancellationToken ct)
    {
        var check = new ValidationCheck
        {
            RunId           = Guid.Empty,
            CheckType       = ValidationCheckType.User,
            SourceReference = entry.SourceUpn,
            TargetReference = entry.TargetUpn,
            CheckedAt       = DateTime.UtcNow,
        };

        try
        {
            await graphClient.Users[entry.TargetUpn].GetAsync(cancellationToken: ct);
            check.Outcome = ValidationOutcome.Pass;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            check.Outcome      = ValidationOutcome.Fail;
            check.ErrorMessage = $"User '{entry.TargetUpn}' not found in target tenant (404).";
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Access denied checking user '{entry.TargetUpn}' ({ex.ResponseStatusCode}).";
        }
        catch (Exception ex)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Error checking user '{entry.TargetUpn}': {ex.Message}";
            _logger.LogWarning(ex, "ValidationWorker: error checking user {TargetUpn}.", entry.TargetUpn);
        }

        return check;
    }

    /// <summary>
    /// Hybrid-target check: the migrated user should report
    /// <c>onPremisesSyncEnabled=true</c> once the AD handoff kit has been run
    /// and an Entra Connect cycle completed. Cloud-only is a warning (pending
    /// handoff), missing user is a fail.
    /// </summary>
    private async Task<ValidationCheck> CheckDirectoryLinkAsync(
        Microsoft.Graph.GraphServiceClient graphClient,
        MailboxMigrationEntry entry,
        CancellationToken ct)
    {
        var check = new ValidationCheck
        {
            RunId           = Guid.Empty,
            CheckType       = ValidationCheckType.DirectorySync,
            SourceReference = entry.SourceUpn,
            TargetReference = entry.TargetUpn,
            CheckedAt       = DateTime.UtcNow,
        };

        try
        {
            var user = await graphClient.Users[entry.TargetUpn].GetAsync(req =>
            {
                req.QueryParameters.Select = ["id", "onPremisesSyncEnabled"];
            }, ct);

            if (user?.OnPremisesSyncEnabled == true)
            {
                check.Outcome = ValidationOutcome.Pass;
            }
            else
            {
                check.Outcome      = ValidationOutcome.Warning;
                check.ErrorMessage =
                    $"'{entry.TargetUpn}' is still cloud-only (not directory-synced). Run the batch's " +
                    "hybrid AD handoff kit on-prem, then an Entra Connect sync cycle, and re-validate.";
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            check.Outcome      = ValidationOutcome.Fail;
            check.ErrorMessage = $"User '{entry.TargetUpn}' not found in target tenant (404).";
        }
        catch (Exception ex)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Error checking directory link for '{entry.TargetUpn}': {ex.Message}";
            _logger.LogWarning(ex, "ValidationWorker: error checking directory link for {TargetUpn}.", entry.TargetUpn);
        }

        return check;
    }

    private async Task<ValidationCheck> CheckContentItemAsync(
        Microsoft.Graph.GraphServiceClient graphClient,
        ContentMigrationItem item,
        CancellationToken ct)
    {
        var checkType = string.IsNullOrWhiteSpace(item.OwnerUpn)
            ? ValidationCheckType.SharePoint
            : ValidationCheckType.OneDrive;

        var check = new ValidationCheck
        {
            RunId           = Guid.Empty, // set by FlushChecksAsync
            CheckType       = checkType,
            SourceReference = item.SourceUrl,
            TargetReference = item.TargetUrl,
            CheckedAt       = DateTime.UtcNow,
        };

        try
        {
            if (checkType == ValidationCheckType.SharePoint)
            {
                // Query for the site by its web URL
                var sites = await graphClient.Sites
                    .GetAsync(req =>
                    {
                        req.QueryParameters.Filter = $"webUrl eq '{item.TargetUrl}'";
                    }, ct);

                if (sites?.Value is { Count: > 0 })
                    check.Outcome = ValidationOutcome.Pass;
                else
                {
                    check.Outcome      = ValidationOutcome.Fail;
                    check.ErrorMessage = $"SharePoint site not found at '{item.TargetUrl}' in target tenant.";
                }
            }
            else
            {
                // OneDrive check: verify the owner's drive exists in the target tenant
                var ownerUpn = item.OwnerUpn!;
                await graphClient.Users[ownerUpn].Drive.GetAsync(cancellationToken: ct);
                check.Outcome = ValidationOutcome.Pass;
            }
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            check.Outcome      = ValidationOutcome.Fail;
            check.ErrorMessage = $"Content not found at target '{item.TargetUrl}' (404).";
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Access denied checking target content '{item.TargetUrl}' ({ex.ResponseStatusCode}).";
        }
        catch (Exception ex)
        {
            check.Outcome      = ValidationOutcome.Warning;
            check.ErrorMessage = $"Error checking content '{item.TargetUrl}': {ex.Message}";
            _logger.LogWarning(ex, "ValidationWorker: error checking content item {TargetUrl}.", item.TargetUrl);
        }

        return check;
    }

    // ── Flush helpers ─────────────────────────────────────────────────────────

    private static async Task FlushChecksAsync(
        IValidationRepository repo,
        ValidationRun run,
        List<ValidationCheck> checks,
        int passedCount,
        int failedCount,
        int warningCount,
        IServiceScope scope,
        CancellationToken ct)
    {
        // Assign the run ID to each check
        foreach (var check in checks)
            check.RunId = run.Id;

        await repo.AddChecksAsync(checks, ct);

        // Update run counters mid-flight so the UI can show progress
        run.PassedChecks  = passedCount;
        run.FailedChecks  = failedCount;
        run.WarningChecks = warningCount;
        run.TotalChecks   = passedCount + failedCount + warningCount;

        await repo.SaveAsync(ct);
        checks.Clear();

        // Broadcast progress
        await NotifyValidationSafe(scope, run, null, ct);
    }

    private static async Task NotifyValidationSafe(
        IServiceScope scope,
        ValidationRun run,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IProgressNotifier>();
            await notifier.NotifyValidationProgressAsync(
                run.Id,
                run.ProjectId,
                run.PassedChecks,
                run.FailedChecks,
                run.WarningChecks,
                run.TotalChecks,
                run.Status.ToString(),
                ct);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "ValidationWorker: SignalR notification failed for run {RunId} — ignoring.",
                run.Id);
        }
    }
}
