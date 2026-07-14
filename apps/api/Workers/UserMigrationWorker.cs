using System.Text.Json;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that materialises source users in the target tenant. The
/// transport is selected per batch via <see cref="UserMigrationBatch.Strategy"/>:
/// <list type="bullet">
///   <item><description><c>DirectGraph</c> — Graph <c>POST /users</c> per entry. Plain member accounts.</description></item>
///   <item><description><c>CrossTenantSync</c> — Entra cross-tenant synchronization, <c>provisionOnDemand</c> against the cross-tenant sync app's job.</description></item>
/// </list>
/// Dequeues batch IDs from <see cref="UserMigrationQueue"/>. Re-hydrates any
/// Provisioning batches from the database on startup so in-flight work survives
/// a restart.
/// </summary>
public sealed class UserMigrationWorker : BackgroundService
{
    private readonly UserMigrationQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserMigrationWorker> _logger;

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    public UserMigrationWorker(
        UserMigrationQueue queue,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<UserMigrationWorker> logger)
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
                "UserMigrationWorker: disabled via Workers:Enabled=false — queued batches will not run.");
            return;
        }

        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "UserMigrationWorker: not the primary instance — background processing suppressed.");
            return;
        }

        _logger.LogInformation("UserMigrationWorker started.");
        await RehydrateActiveBatchesAsync(stoppingToken);

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
                await PollActiveBatchesAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                break; // host shutdown — exit quietly instead of failing the service
            }

            if (!hasItem) break;

            while (reader.TryRead(out var batchId))
            {
                _logger.LogInformation("UserMigrationWorker: dequeued batch {BatchId}.", batchId);
                await ProcessBatchAsync(batchId, stoppingToken);
            }
        }

        _logger.LogInformation("UserMigrationWorker stopped.");
    }

    private async Task RehydrateActiveBatchesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUserMigrationRepository>();
            foreach (var batch in await repo.GetActiveBatchesAsync(ct))
            {
                _logger.LogInformation(
                    "UserMigrationWorker: re-hydrating batch {BatchId} ({Status}).",
                    batch.Id, batch.Status);
                _queue.Channel.Writer.TryWrite(batch.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserMigrationWorker: error during startup re-hydration.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("UserMigrationWorker");
        }
    }

    private async Task PollActiveBatchesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUserMigrationRepository>();
            foreach (var batch in await repo.GetActiveBatchesAsync(ct))
                _queue.Channel.Writer.TryWrite(batch.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserMigrationWorker: error during idle poll sweep.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("UserMigrationWorker");
        }
    }

    private async Task ProcessBatchAsync(Guid batchId, CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserMigrationRepository>();

        var batch = await repo.GetBatchWithProjectAsync(batchId, stoppingToken);
        if (batch is null)
        {
            _logger.LogWarning("UserMigrationWorker: batch {BatchId} not found — skipping.", batchId);
            return;
        }

        if (batch.Status is UserMigrationBatchStatus.Completed
                         or UserMigrationBatchStatus.Failed
                         or UserMigrationBatchStatus.Stopped)
        {
            _logger.LogDebug(
                "UserMigrationWorker: batch {BatchId} is in terminal state {Status} — skipping.",
                batchId, batch.Status);
            return;
        }

        var project = batch.Project;
        if (project?.TargetTenant is null)
        {
            await FailBatchAsync(scope, repo, batch,
                "Target tenant not found for this batch.", stoppingToken);
            return;
        }

        if (batch.Strategy == UserMigrationStrategy.CrossTenantSync)
            await ProcessCrossTenantSyncAsync(scope, repo, batch, stoppingToken);
        else
            await ProcessDirectGraphAsync(scope, repo, batch, stoppingToken);
    }

    /// <summary>
    /// Direct Graph <c>POST /users</c> path. Iterates entries one at a time and
    /// creates a member account per source→target UPN pair.
    /// </summary>
    private async Task ProcessDirectGraphAsync(
        IServiceScope scope,
        IUserMigrationRepository repo,
        UserMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var batchId = batch.Id;
        var project = batch.Project!;
        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");
        Microsoft.Graph.GraphServiceClient? targetGraph = null;

        if (!isMock)
        {
            try
            {
                var keyVault     = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
                var graphFactory = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();

                var (certB64, certPw, secret) = await keyVault.LoadCredentialsAsync(project.TargetTenant!.Id, stoppingToken);
                targetGraph = graphFactory.CreateForTenant(project.TargetTenant, certB64, certPw, secret);
            }
            catch (Exception ex)
            {
                await FailBatchAsync(scope, repo, batch,
                    $"Target Graph credentials not available: {ex.Message}", stoppingToken);
                return;
            }
        }

        var entries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var pendingEntries = entries
            .Where(e => e.Status is UserMigrationEntryStatus.Queued or UserMigrationEntryStatus.Provisioning)
            .ToList();

        // Build a source-UPN → (DisplayName, ProxyAddresses) lookup from the
        // most recent completed source scan so we can carry the user's real
        // display name AND mailbox aliases over to the target tenant.
        var sourceUserInfo = await BuildSourceUserLookupAsync(
            scope, project.SourceTenantId, stoppingToken);

        _logger.LogInformation(
            "UserMigrationWorker: batch {BatchId} (DirectGraph) — {Pending} entries to provision ({Total} total).",
            batchId, pendingEntries.Count, entries.Count);

        foreach (var entry in pendingEntries)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Re-check batch status (may have been stopped via API)
            var freshBatch = await repo.GetBatchByIdAsync(batchId, stoppingToken);
            if (freshBatch?.Status is UserMigrationBatchStatus.Stopped)
            {
                _logger.LogInformation(
                    "UserMigrationWorker: batch {BatchId} was stopped — halting processing.", batchId);
                break;
            }

            entry.Status      = UserMigrationEntryStatus.Provisioning;
            entry.LastUpdated = DateTime.UtcNow;
            await repo.SaveAsync(stoppingToken);
            await NotifyBatchProgressSafe(scope, batch, stoppingToken);

            _logger.LogInformation(
                "UserMigrationWorker: provisioning {SourceUpn} → {TargetUpn} (entry {EntryId}) [mock={IsMock}].",
                entry.SourceUpn, entry.TargetUpn, entry.Id, isMock);

            try
            {
                if (isMock)
                {
                    await Task.Delay(200, stoppingToken);
                    entry.TargetObjectId = Guid.NewGuid().ToString();
                }
                else
                {
                    var mailNickname = BuildMailNickname(entry.TargetUpn);
                    var displayName  = ResolveDisplayName(entry, sourceUserInfo);

                    var newUser = new User
                    {
                        AccountEnabled    = true,
                        DisplayName       = displayName,
                        MailNickname      = mailNickname,
                        UserPrincipalName = entry.TargetUpn,
                        PasswordProfile   = new PasswordProfile
                        {
                            Password                             = GenerateInitialPassword(),
                            ForceChangePasswordNextSignIn        = true,
                        },
                    };

                    var created = await targetGraph!.Users.PostAsync(newUser, cancellationToken: stoppingToken);
                    entry.TargetObjectId = created?.Id;

                    // Do NOT try to stamp source SMTP aliases here: proxyAddresses is
                    // read-only on the Graph user resource (confirmed live — Graph 400
                    // "Property 'proxyAddresses' is read-only and cannot be set", for
                    // any value, verified domain or not). Aliases are mastered by
                    // Exchange: on a plain member account there is no EXO recipient to
                    // stamp them on until the user is mail-enabled or licensed, and
                    // users whose mailbox migrates get their addresses from the
                    // MailUser provisioning in the mailbox flow instead. Surface what
                    // was skipped so the operator can add aliases post-mail-enablement.
                    var skippedAliases = BuildTargetProxyAddresses(entry, sourceUserInfo)
                        .Where(a => !a.Equals($"SMTP:{entry.TargetUpn}", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (skippedAliases.Count > 0)
                        _logger.LogInformation(
                            "UserMigrationWorker: {TargetUpn} created without {Count} source alias(es) " +
                            "({Aliases}) — proxyAddresses cannot be set via Graph; add them via Exchange " +
                            "once the account is mail-enabled.",
                            entry.TargetUpn, skippedAliases.Count, string.Join(", ", skippedAliases));
                }

                entry.Status       = UserMigrationEntryStatus.Provisioned;
                entry.ErrorMessage = null;
                entry.LastUpdated  = DateTime.UtcNow;
                batch.ProvisionedUsers++;

                _logger.LogInformation(
                    "UserMigrationWorker: provisioned {TargetUpn} (objectId={ObjectId}).",
                    entry.TargetUpn, entry.TargetObjectId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "UserMigrationWorker: cancellation requested during provisioning of {TargetUpn}.",
                    entry.TargetUpn);
                break;
            }
            catch (ODataError ex)
            {
                entry.Status       = UserMigrationEntryStatus.Failed;
                entry.ErrorMessage = $"Graph {ex.ResponseStatusCode}: {ex.Error?.Message ?? ex.Message}";
                entry.LastUpdated  = DateTime.UtcNow;
                batch.FailedUsers++;

                _logger.LogWarning(ex,
                    "UserMigrationWorker: Graph error provisioning {TargetUpn} (entry {EntryId}).",
                    entry.TargetUpn, entry.Id);
            }
            catch (Exception ex)
            {
                entry.Status       = UserMigrationEntryStatus.Failed;
                entry.ErrorMessage = ex.Message;
                entry.LastUpdated  = DateTime.UtcNow;
                batch.FailedUsers++;

                _logger.LogWarning(ex,
                    "UserMigrationWorker: unexpected error provisioning {TargetUpn} (entry {EntryId}).",
                    entry.TargetUpn, entry.Id);
            }

            batch.LastUpdatedAt = DateTime.UtcNow;
            await repo.SaveAsync(stoppingToken);
            await NotifyBatchProgressSafe(scope, batch, stoppingToken);
        }

        await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
    }

    /// <summary>
    /// Entra cross-tenant sync path. The SOURCE tenant hosts the sync app +
    /// Azure2Azure synchronization job (push model); per-user provisioning is
    /// triggered via Graph
    /// <c>POST /servicePrincipals/{id}/synchronization/jobs/{jobId}/provisionOnDemand</c>.
    /// Mock mode runs the simulator. Live mode resolves each source UPN to an
    /// object ID, ensures the sync job is running, then issues provisionOnDemand
    /// calls in chunks of 5 (Graph's per-call limit).
    /// </summary>
    private async Task ProcessCrossTenantSyncAsync(
        IServiceScope scope,
        IUserMigrationRepository repo,
        UserMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var batchId = batch.Id;
        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");

        if (isMock)
        {
            _logger.LogInformation(
                "UserMigrationWorker: batch {BatchId} (CrossTenantSync, mock) — simulating provisionOnDemand.", batchId);
            await SimulateCrossTenantSyncAsync(scope, repo, batch, stoppingToken);
            return;
        }

        var project = batch.Project!;
        if (project.SourceTenant is null)
        {
            await FailBatchAsync(scope, repo, batch,
                "Source tenant not loaded for this batch — required for CrossTenantSync.", stoppingToken);
            return;
        }

        var syncClient = scope.ServiceProvider.GetRequiredService<IGraphSyncClient>();

        // Target Graph client — needed after each successful provisionOnDemand to
        // rename the user's UPN from the #EXT# guest format to the clean target UPN.
        Microsoft.Graph.GraphServiceClient targetGraph;
        try
        {
            var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
            var graphFactory = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();
            var (certB64, certPw, secret) = await keyVault.LoadCredentialsAsync(project.TargetTenant!.Id, stoppingToken);
            targetGraph = graphFactory.CreateForTenant(project.TargetTenant, certB64, certPw, secret);
        }
        catch (Exception ex)
        {
            await FailBatchAsync(scope, repo, batch,
                $"Target Graph credentials not available: {ex.Message}", stoppingToken);
            return;
        }

        // ── 1. Ensure the sync job is started; persist composite job ID + rule ID
        string compositeJobId;
        string ruleId;
        try
        {
            if (string.IsNullOrWhiteSpace(batch.CrossTenantSyncJobId))
            {
                var jobResult = await syncClient.StartSyncJobAsync(
                    project.SourceTenant,
                    appClientId: null, // discover via SP scan — wizard apps don't share appClientId with our config
                    stoppingToken);
                batch.CrossTenantSyncJobId = jobResult.CompositeJobId;
                _logger.LogInformation(
                    "UserMigrationWorker: batch {BatchId} attached to sync job {CompositeJobId}.",
                    batchId, jobResult.CompositeJobId);
            }
            else
            {
                // Re-use the composite job from a prior run; just make sure it's started.
                await syncClient.StartSyncJobAsync(project.SourceTenant, appClientId: null, stoppingToken);
            }

            compositeJobId = batch.CrossTenantSyncJobId!;

            ruleId = batch.CrossTenantSyncRuleId
                  ?? await syncClient.GetSyncJobSchemaRuleIdAsync(project.SourceTenant, compositeJobId, stoppingToken);
            batch.CrossTenantSyncRuleId = ruleId;

            await repo.SaveAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            await FailBatchAsync(scope, repo, batch,
                $"Could not initialise the cross-tenant sync job: {ex.Message}", stoppingToken);
            return;
        }

        // ── 2. Resolve each entry's source object ID and snapshot pending entries.
        var entries = (await repo.GetEntriesByBatchAsync(batchId, stoppingToken)).ToList();
        var pending = entries
            .Where(e => e.Status is UserMigrationEntryStatus.Queued or UserMigrationEntryStatus.Provisioning)
            .ToList();

        var idLookup = new Dictionary<Guid, string>(); // entry.Id -> source object ID
        foreach (var entry in pending)
        {
            if (stoppingToken.IsCancellationRequested) break;
            try
            {
                var objectId = await syncClient.ResolveUserObjectIdAsync(
                    project.SourceTenant, entry.SourceUpn, stoppingToken);
                if (string.IsNullOrWhiteSpace(objectId))
                {
                    entry.Status = UserMigrationEntryStatus.Skipped;
                    entry.ErrorMessage = $"Source user '{entry.SourceUpn}' not found in source tenant.";
                    entry.LastUpdated = DateTime.UtcNow;
                }
                else
                {
                    idLookup[entry.Id] = objectId;
                    entry.Status = UserMigrationEntryStatus.Provisioning;
                    entry.LastUpdated = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                entry.Status = UserMigrationEntryStatus.Failed;
                entry.ErrorMessage = $"Could not resolve source UPN: {ex.Message}";
                entry.LastUpdated = DateTime.UtcNow;
            }
        }
        await repo.SaveAsync(stoppingToken);
        await NotifyBatchProgressSafe(scope, batch, stoppingToken);

        // ── 3. Issue provisionOnDemand in chunks of 5.
        var resolvable = pending.Where(e => idLookup.ContainsKey(e.Id)).ToList();
        _logger.LogInformation(
            "UserMigrationWorker: batch {BatchId} (CrossTenantSync) — provisionOnDemand for {Count} resolved user(s).",
            batchId, resolvable.Count);

        const int ChunkSize = 5;
        for (int i = 0; i < resolvable.Count; i += ChunkSize)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var freshBatch = await repo.GetBatchByIdAsync(batchId, stoppingToken);
            if (freshBatch?.Status is UserMigrationBatchStatus.Stopped)
            {
                _logger.LogInformation(
                    "UserMigrationWorker: batch {BatchId} was stopped — halting CrossTenantSync.", batchId);
                break;
            }

            var chunk = resolvable.Skip(i).Take(ChunkSize).ToList();
            var chunkIds = chunk.Select(e => idLookup[e.Id]).ToList();

            ProvisionOnDemandResult result;
            try
            {
                result = await syncClient.ProvisionOnDemandAsync(
                    project.SourceTenant, compositeJobId, ruleId, chunkIds, stoppingToken);
            }
            catch (Exception ex)
            {
                // Whole-chunk failure — mark every entry in it Failed.
                foreach (var entry in chunk)
                {
                    entry.Status = UserMigrationEntryStatus.Failed;
                    entry.ErrorMessage = $"provisionOnDemand call failed: {ex.Message}";
                    entry.LastUpdated = DateTime.UtcNow;
                    batch.FailedUsers++;
                }
                _logger.LogWarning(ex,
                    "UserMigrationWorker: batch {BatchId} provisionOnDemand chunk failed.", batchId);
                batch.LastUpdatedAt = DateTime.UtcNow;
                await repo.SaveAsync(stoppingToken);
                await NotifyBatchProgressSafe(scope, batch, stoppingToken);
                continue;
            }

            // Map per-user outcomes back onto entries by source object ID.
            var byObjectId = result.Users.ToDictionary(u => u.UserObjectId, u => u, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in chunk)
            {
                var sourceId = idLookup[entry.Id];
                if (!byObjectId.TryGetValue(sourceId, out var outcome))
                {
                    entry.Status = UserMigrationEntryStatus.Failed;
                    entry.ErrorMessage = "No provisionOnDemand result for this user.";
                    entry.LastUpdated = DateTime.UtcNow;
                    batch.FailedUsers++;
                    continue;
                }

                switch (outcome.Status)
                {
                    case "Success":
                        entry.TargetObjectId = outcome.TargetObjectId ?? entry.TargetObjectId;
                        // Rename the #EXT# guest UPN to the clean target UPN.
                        // PATCH by object ID — the #EXT# UPN contains '#' chars
                        // that break Graph URL routing.
                        var renameError = await RenameTargetUpnAsync(
                            targetGraph, entry, stoppingToken);
                        if (renameError is not null)
                        {
                            entry.Status = UserMigrationEntryStatus.Failed;
                            entry.ErrorMessage = $"Provisioned, but UPN rename failed: {renameError}";
                            batch.FailedUsers++;
                        }
                        else
                        {
                            entry.Status = UserMigrationEntryStatus.Provisioned;
                            entry.ErrorMessage = null;
                            batch.ProvisionedUsers++;
                        }
                        break;
                    case "Skipped":
                        entry.Status = UserMigrationEntryStatus.Skipped;
                        entry.ErrorMessage = outcome.ErrorMessage ?? "Skipped by Graph provisioning engine.";
                        break;
                    default:
                        entry.Status = UserMigrationEntryStatus.Failed;
                        entry.ErrorMessage = outcome.ErrorMessage ?? $"Provisioning status: {outcome.Status}.";
                        batch.FailedUsers++;
                        break;
                }
                entry.LastUpdated = DateTime.UtcNow;
            }

            batch.LastUpdatedAt = DateTime.UtcNow;
            await repo.SaveAsync(stoppingToken);
            await NotifyBatchProgressSafe(scope, batch, stoppingToken);
        }

        await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
    }

    /// <summary>
    /// After provisionOnDemand creates the target user as a guest with a
    /// <c>user_source.com#EXT#@target.onmicrosoft.com</c> UPN, PATCH it to the
    /// clean target UPN. Identifies the user by object ID (the #EXT# UPN
    /// contains '#' chars that break Graph URL routing). Retries with backoff
    /// because the user may not be immediately PATCH-able after provisioning.
    /// Returns null on success, or an error message on terminal failure.
    /// </summary>
    private async Task<string?> RenameTargetUpnAsync(
        Microsoft.Graph.GraphServiceClient targetGraph,
        UserMigrationEntry entry,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetObjectId))
            return "provisionOnDemand did not return a target object ID.";

        var delaysSec = new[] { 10, 15, 20, 30, 30 };

        // Initial wait for Graph replication after provisioning.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        for (int attempt = 0; attempt < delaysSec.Length; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "UserMigrationWorker: renaming target UPN for {SourceUpn} → {TargetUpn} (objectId={ObjectId}, attempt {Attempt}/{Max}).",
                    entry.SourceUpn, entry.TargetUpn, entry.TargetObjectId, attempt + 1, delaysSec.Length);

                await targetGraph.Users[entry.TargetObjectId].PatchAsync(
                    new Microsoft.Graph.Models.User { UserPrincipalName = entry.TargetUpn },
                    cancellationToken: ct);

                _logger.LogInformation(
                    "UserMigrationWorker: UPN rename succeeded — {SourceUpn} → {TargetUpn}.",
                    entry.SourceUpn, entry.TargetUpn);
                return null;
            }
            catch (Exception ex) when (attempt < delaysSec.Length - 1
                && (ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning(
                    "UserMigrationWorker: UPN rename attempt {Attempt} for {SourceUpn} — user not yet available; retrying in {Delay}s.",
                    attempt + 1, entry.SourceUpn, delaysSec[attempt]);
                await Task.Delay(TimeSpan.FromSeconds(delaysSec[attempt]), ct);
            }
            catch (ODataError ex)
            {
                return $"Graph {ex.ResponseStatusCode}: {ex.Error?.Message ?? ex.Message}";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        return "User did not become available for UPN rename within the retry window.";
    }

    /// <summary>Mock-mode CTS: snap entries through Provisioning → Provisioned quickly.</summary>
    private async Task SimulateCrossTenantSyncAsync(
        IServiceScope scope,
        IUserMigrationRepository repo,
        UserMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var entries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var pending = entries
            .Where(e => e.Status is UserMigrationEntryStatus.Queued or UserMigrationEntryStatus.Provisioning)
            .ToList();

        foreach (var entry in pending)
        {
            entry.Status = UserMigrationEntryStatus.Provisioning;
            entry.LastUpdated = DateTime.UtcNow;
        }
        await repo.SaveAsync(stoppingToken);
        await NotifyBatchProgressSafe(scope, batch, stoppingToken);

        await Task.Delay(500, stoppingToken);

        foreach (var entry in pending)
        {
            entry.Status = UserMigrationEntryStatus.Provisioned;
            entry.TargetObjectId ??= Guid.NewGuid().ToString();
            entry.ErrorMessage = null;
            entry.LastUpdated = DateTime.UtcNow;
            batch.ProvisionedUsers++;
        }
        batch.LastUpdatedAt = DateTime.UtcNow;
        await repo.SaveAsync(stoppingToken);

        await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
    }

    /// <summary>
    /// Recompute provisioned/failed/skipped counts from current entries and
    /// transition the batch to a terminal status. Shared by both DirectGraph and
    /// CrossTenantSync finishers.
    /// </summary>
    private async Task FinalizeFromEntriesAsync(
        IServiceScope scope,
        IUserMigrationRepository repo,
        UserMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var allEntries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var anyOpen = allEntries.Any(e =>
            e.Status is UserMigrationEntryStatus.Queued or UserMigrationEntryStatus.Provisioning);
        if (anyOpen || stoppingToken.IsCancellationRequested) return;

        batch.ProvisionedUsers = allEntries.Count(e => e.Status == UserMigrationEntryStatus.Provisioned);
        batch.FailedUsers      = allEntries.Count(e => e.Status == UserMigrationEntryStatus.Failed);
        batch.SkippedUsers     = allEntries.Count(e => e.Status == UserMigrationEntryStatus.Skipped);

        // Terminal status is decided against *attempted* users only (total − skipped).
        // Skipped entries don't count toward failure, so a batch with no real failures
        // lands in Completed even if every remaining entry was skipped.
        var attempted = batch.TotalUsers - batch.SkippedUsers;
        batch.Status = (attempted > 0 && batch.FailedUsers == attempted)
            ? UserMigrationBatchStatus.Failed
            : UserMigrationBatchStatus.Completed;
        batch.CompletedAt = DateTime.UtcNow;

        if (batch.FailedUsers > 0 && batch.ProvisionedUsers > 0)
            batch.ErrorMessage = $"{batch.FailedUsers} of {attempted} attempted user(s) failed to provision.";
        else if (batch.Status == UserMigrationBatchStatus.Completed && batch.SkippedUsers > 0 && batch.ProvisionedUsers == 0)
            batch.ErrorMessage = $"All {batch.SkippedUsers} user(s) were skipped — nothing to provision.";

        await repo.SaveAsync(stoppingToken);
        await NotifyAndAuditAsync(scope, batch, stoppingToken);

        _logger.LogInformation(
            "UserMigrationWorker: batch {BatchId} ({Strategy}) finished — {Status}. " +
            "Provisioned: {Provisioned}, Failed: {Failed}, Skipped: {Skipped}, Total: {Total}.",
            batch.Id, batch.Strategy, batch.Status,
            batch.ProvisionedUsers, batch.FailedUsers, batch.SkippedUsers, batch.TotalUsers);
    }

    private async Task FailBatchAsync(
        IServiceScope scope,
        IUserMigrationRepository repo,
        UserMigrationBatch batch,
        string error,
        CancellationToken ct)
    {
        _logger.LogWarning("UserMigrationWorker: batch {BatchId} marked Failed — {Error}", batch.Id, error);
        batch.Status       = UserMigrationBatchStatus.Failed;
        batch.CompletedAt  = DateTime.UtcNow;
        batch.ErrorMessage = error;
        await repo.SaveAsync(ct);
        await NotifyAndAuditAsync(scope, batch, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Source-scan info we carry forward when provisioning a target user.</summary>
    private sealed record SourceUserInfo(string DisplayName, IReadOnlyList<string> ProxyAddresses);

    private async Task<Dictionary<string, SourceUserInfo>> BuildSourceUserLookupAsync(
        IServiceScope scope, Guid sourceTenantId, CancellationToken ct)
    {
        try
        {
            var scanRepo = scope.ServiceProvider.GetRequiredService<IScanRepository>();
            var latestScan = await scanRepo.GetLatestCompletedAsync(sourceTenantId, ct);
            if (latestScan is null) return new(StringComparer.OrdinalIgnoreCase);

            var users = await scanRepo.GetUsersAsync(latestScan.Id, ct);
            return users
                .Where(u => !string.IsNullOrWhiteSpace(u.Upn))
                .GroupBy(u => u.Upn, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var first = g.First();
                        return new SourceUserInfo(
                            first.DisplayName ?? string.Empty,
                            first.ProxyAddresses ?? new List<string>());
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "UserMigrationWorker: could not load source user lookup for tenant {TenantId}; " +
                "falling back to UPN local-part.", sourceTenantId);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ResolveDisplayName(
        UserMigrationEntry entry, IReadOnlyDictionary<string, SourceUserInfo> lookup)
    {
        if (!string.IsNullOrWhiteSpace(entry.SourceUpn)
            && lookup.TryGetValue(entry.SourceUpn, out var info)
            && !string.IsNullOrWhiteSpace(info.DisplayName))
        {
            return info.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(entry.SourceUpn))
            return entry.SourceUpn.Split('@')[0];

        return entry.TargetUpn;
    }

    /// <summary>
    /// Build the <c>proxyAddresses</c> list to PATCH onto the new target user.
    /// The target UPN goes in as the primary (uppercase <c>SMTP:</c>); the
    /// source UPN and any other source SMTP aliases are added as secondary
    /// (lowercase <c>smtp:</c>) so mail to the source domain continues to
    /// route after the source domain is verified in the target tenant.
    /// Duplicates and non-SMTP entries are filtered out.
    /// </summary>
    private static List<string> BuildTargetProxyAddresses(
        UserMigrationEntry entry, IReadOnlyDictionary<string, SourceUserInfo> lookup)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        // Primary — target UPN
        var primary = $"SMTP:{entry.TargetUpn}";
        result.Add(primary);
        seen.Add(entry.TargetUpn);

        // Source UPN as a secondary alias so mail to user@source.com routes here.
        if (!string.IsNullOrWhiteSpace(entry.SourceUpn) && seen.Add(entry.SourceUpn))
            result.Add($"smtp:{entry.SourceUpn}");

        // Any additional SMTP aliases captured by the source scan.
        if (lookup.TryGetValue(entry.SourceUpn ?? string.Empty, out var info))
        {
            foreach (var raw in info.ProxyAddresses)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var colon = raw.IndexOf(':');
                if (colon < 0) continue;

                var prefix = raw[..colon];
                var addr = raw[(colon + 1)..].Trim();
                if (!prefix.Equals("smtp", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(addr) || !seen.Add(addr)) continue;

                // Always re-stamp as secondary; the target UPN is the only primary.
                result.Add($"smtp:{addr}");
            }
        }

        return result;
    }

    private static string BuildMailNickname(string upn)
    {
        var localPart = upn.Split('@')[0];
        // Graph requires mailNickname to be <= 64 chars, printable ASCII, no whitespace.
        return new string(localPart.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
    }

    private static string GenerateInitialPassword()
    {
        // Random 16-char password; the user is forced to change it on first sign-in.
        var buf = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return "Aa1!" + Convert.ToBase64String(buf).Replace("+", "x").Replace("/", "y").Replace("=", "");
    }

    private async Task NotifyAndAuditAsync(IServiceScope scope, UserMigrationBatch batch, CancellationToken ct)
    {
        await NotifyBatchProgressSafe(scope, batch, ct);
        await WriteTerminalAuditEventAsync(scope, batch, ct);
    }

    private async Task WriteTerminalAuditEventAsync(IServiceScope scope, UserMigrationBatch batch, CancellationToken ct)
    {
        try
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
            var action = batch.Status switch
            {
                UserMigrationBatchStatus.Completed => "USER_MIGRATION_BATCH_COMPLETED",
                UserMigrationBatchStatus.Failed    => "USER_MIGRATION_BATCH_FAILED",
                UserMigrationBatchStatus.Stopped   => "USER_MIGRATION_BATCH_STOPPED",
                _                                  => $"USER_MIGRATION_BATCH_{batch.Status.ToString().ToUpperInvariant()}",
            };
            await audit.AddAsync(new Models.AuditEvent
            {
                Action    = action,
                Resource  = $"projects/{batch.ProjectId}/user-migrations/{batch.Id}",
                Actor     = "system",
                ProjectId = batch.ProjectId,
                Outcome   = batch.Status == UserMigrationBatchStatus.Completed ? "success" : "failure",
                Details   = JsonSerializer.Serialize(new
                {
                    batchId     = batch.Id,
                    strategy    = batch.Strategy.ToCamelCase(),
                    provisioned = batch.ProvisionedUsers,
                    failed      = batch.FailedUsers,
                    skipped     = batch.SkippedUsers,
                    total       = batch.TotalUsers,
                    error       = batch.ErrorMessage,
                }),
            }, ct);
            await audit.SaveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "UserMigrationWorker: failed to write audit event for batch {BatchId} — ignoring.", batch.Id);
        }
    }

    private static async Task NotifyBatchProgressSafe(
        IServiceScope scope,
        UserMigrationBatch batch,
        CancellationToken ct)
    {
        try
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IProgressNotifier>();
            await notifier.NotifyUserMigrationProgressAsync(
                batch.Id,
                batch.ProjectId,
                batch.ProvisionedUsers,
                batch.TotalUsers,
                batch.FailedUsers,
                batch.Status.ToCamelCase(),
                ct);
        }
        catch (Exception)
        {
            // SignalR failure must never crash the worker
        }
    }
}
