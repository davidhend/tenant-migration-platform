using System.Text.Json;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that copies mail from source to target tenant via
/// Microsoft Graph. Processes one user at a time within each batch to stay
/// within Graph API throttling limits.
///
/// Dequeues batch IDs from <see cref="MailboxMigrationQueue"/> and processes
/// each entry sequentially. Re-hydrates any Syncing batches from the database
/// on startup so in-flight work survives a service restart.
/// </summary>
public class MailboxMigrationWorker : BackgroundService
{
    private readonly MailboxMigrationQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MailboxMigrationWorker> _logger;

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    public MailboxMigrationWorker(
        MailboxMigrationQueue queue,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<MailboxMigrationWorker> logger)
    {
        _queue = queue;
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Workers run by default; Database:AutoMigrate is a schema-bootstrap flag, not a
        // worker switch. Only an explicit Workers:Enabled=false disables processing.
        if (!_configuration.GetValue("Workers:Enabled", true))
        {
            _logger.LogWarning(
                "MailboxMigrationWorker: Workers:Enabled is false — worker is disabled and " +
                "enqueued batches will NOT be processed.");
            return;
        }

        // Single-instance safety: only the primary instance processes batches
        // (see SingleInstanceGuard) — a secondary would double-poll and create
        // duplicate EXO migration batches.
        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "MailboxMigrationWorker: not the primary instance — background processing suppressed.");
            return;
        }

        _logger.LogInformation("MailboxMigrationWorker started (Graph mail copy mode).");

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
                _logger.LogInformation("MailboxMigrationWorker: dequeued batch {BatchId}.", batchId);
                await ProcessBatchAsync(batchId, stoppingToken);
            }
        }

        _logger.LogInformation("MailboxMigrationWorker stopped.");
    }

    private async Task RehydrateActiveBatchesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMailboxMigrationRepository>();
            var active = await repo.GetActiveBatchesAsync(ct);

            foreach (var batch in active)
            {
                _logger.LogInformation(
                    "MailboxMigrationWorker: re-hydrating batch {BatchId} ({Status}).",
                    batch.Id, batch.Status);
                _queue.Channel.Writer.TryWrite(batch.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MailboxMigrationWorker: error during startup re-hydration.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("MailboxMigrationWorker");
        }
    }

    private async Task PollActiveBatchesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMailboxMigrationRepository>();
            var active = await repo.GetActiveBatchesAsync(ct);

            foreach (var batch in active)
                _queue.Channel.Writer.TryWrite(batch.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MailboxMigrationWorker: error during idle poll sweep.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("MailboxMigrationWorker");
        }
    }

    private async Task ProcessBatchAsync(Guid batchId, CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMailboxMigrationRepository>();

        var batch = await repo.GetBatchWithProjectAsync(batchId, stoppingToken);
        if (batch is null)
        {
            _logger.LogWarning("MailboxMigrationWorker: batch {BatchId} not found — skipping.", batchId);
            return;
        }

        if (batch.Status is BatchStatus.Completed or BatchStatus.Failed
            or BatchStatus.Stopped or BatchStatus.Draft)
        {
            _logger.LogDebug(
                "MailboxMigrationWorker: batch {BatchId} is in state {Status} — skipping.",
                batchId, batch.Status);
            return;
        }

        var project = batch.Project;
        if (project?.SourceTenant is null || project.TargetTenant is null)
        {
            _logger.LogWarning(
                "MailboxMigrationWorker: batch {BatchId} missing source or target tenant — marking Failed.",
                batchId);
            batch.Status = BatchStatus.Failed;
            batch.CompletedAt = DateTime.UtcNow;
            batch.ErrorMessage = "Source or target tenant not found for this batch.";
            await repo.SaveAsync(stoppingToken);
            await NotifyAndAuditAsync(scope, batch, stoppingToken);
            return;
        }

        if (batch.Strategy == MailboxMigrationStrategy.NativeMrs)
            await ProcessNativeMrsAsync(scope, repo, batch, stoppingToken);
        else
            await ProcessGraphCopyAsync(scope, repo, batch, stoppingToken);
    }

    /// <summary>
    /// Per-message Graph copy. Iterates entries one at a time and calls
    /// <see cref="IGraphMailCopyService"/>. Slow but requires no EXO infra.
    /// </summary>
    private async Task ProcessGraphCopyAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var batchId = batch.Id;
        var project = batch.Project!;
        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");

        var mailCopy = scope.ServiceProvider.GetRequiredService<IGraphMailCopyService>();
        Microsoft.Graph.GraphServiceClient? sourceGraph = null, targetGraph = null;

        if (!isMock)
        {
            var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
            var graphFactory = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();

            try
            {
                var (srcCert, srcPw, srcSecret) = await keyVault.LoadCredentialsAsync(project.SourceTenant!.Id, stoppingToken);
                sourceGraph = graphFactory.CreateForTenant(project.SourceTenant, srcCert, srcPw, srcSecret);

                var (tgtCert, tgtPw, tgtSecret) = await keyVault.LoadCredentialsAsync(project.TargetTenant!.Id, stoppingToken);
                targetGraph = graphFactory.CreateForTenant(project.TargetTenant, tgtCert, tgtPw, tgtSecret);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MailboxMigrationWorker: failed to build Graph clients for batch {BatchId} — marking Failed.",
                    batchId);
                batch.Status = BatchStatus.Failed;
                batch.CompletedAt = DateTime.UtcNow;
                batch.ErrorMessage = $"Failed to build Graph credentials: {ex.Message}";
                await repo.SaveAsync(stoppingToken);
                await NotifyAndAuditAsync(scope, batch, stoppingToken);
                return;
            }
        }

        var entries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var pendingEntries = entries
            .Where(e => e.Status is MailboxMigrationStatus.Queued or MailboxMigrationStatus.Syncing)
            .ToList();

        _logger.LogInformation(
            "MailboxMigrationWorker: batch {BatchId} (GraphCopy) — {Pending} entries to process ({Total} total).",
            batchId, pendingEntries.Count, entries.Count);

        foreach (var entry in pendingEntries)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Read through a FRESH scope/DbContext — re-reading via this scope's context
            // returns the tracked (stale) instance and never sees the controller's Stop write.
            using (var checkScope = _services.CreateScope())
            {
                var checkRepo = checkScope.ServiceProvider.GetRequiredService<IMailboxMigrationRepository>();
                var freshBatch = await checkRepo.GetBatchByIdAsync(batchId, stoppingToken);
                if (freshBatch?.Status is BatchStatus.Stopped)
                {
                    _logger.LogInformation(
                        "MailboxMigrationWorker: batch {BatchId} was stopped — halting processing.", batchId);
                    break;
                }
            }

            entry.Status = MailboxMigrationStatus.Syncing;
            entry.LastUpdated = DateTime.UtcNow;
            await repo.SaveAsync(stoppingToken);
            await NotifyBatchProgressSafe(scope, batch, stoppingToken);

            _logger.LogInformation(
                "MailboxMigrationWorker: copying mail for {SourceUpn} → {TargetUpn} (entry {EntryId}) [mock={IsMock}].",
                entry.SourceUpn, entry.TargetUpn, entry.Id, isMock);

            try
            {
                if (isMock)
                {
                    await Task.Delay(200, stoppingToken);
                    var syntheticTotal = 120 + (Math.Abs(entry.SourceUpn.GetHashCode()) % 500);
                    entry.TotalMessages = syntheticTotal;
                    entry.MessagesCopied = syntheticTotal;
                    entry.ItemsSyncedPercent = 100;
                    entry.LastUpdated = DateTime.UtcNow;
                    _logger.LogInformation(
                        "MailboxMigrationWorker (mock): simulated copy of {Total} messages for {SourceUpn}.",
                        syntheticTotal, entry.SourceUpn);
                }
                else
                {
                    var result = await mailCopy.CopyUserMailAsync(
                        sourceGraph!,
                        targetGraph!,
                        entry.SourceUpn,
                        entry.TargetUpn,
                        batch.TargetFolderName,
                        progress =>
                        {
                            entry.MessagesCopied = progress.MessagesCopied;
                            entry.TotalMessages = progress.TotalMessages;
                            entry.ItemsSyncedPercent = progress.TotalMessages > 0
                                ? Math.Round((double)progress.MessagesCopied / progress.TotalMessages * 100, 1)
                                : 0;
                            entry.LastUpdated = DateTime.UtcNow;
                        },
                        stoppingToken);

                    entry.MessagesCopied = result.MessagesCopied;
                    entry.TotalMessages  = result.TotalMessages;

                    // A copy that dropped messages/attachments must not read as a clean sync.
                    if (result.MessagesFailed > 0 || result.AttachmentsSkipped > 0)
                    {
                        var parts = new List<string>();
                        if (result.MessagesFailed > 0)
                            parts.Add($"{result.MessagesFailed} message(s) failed to copy");
                        if (result.AttachmentsSkipped > 0)
                            parts.Add($"{result.AttachmentsSkipped} attachment(s) skipped (item/reference attachments cannot be copied via Graph)");
                        entry.ErrorMessage = string.Join("; ", parts)
                            + (string.IsNullOrWhiteSpace(result.FirstError) ? "" : $". First error: {result.FirstError}");
                    }
                }

                entry.Status = MailboxMigrationStatus.Synced;
                entry.ItemsSyncedPercent = entry.TotalMessages > 0
                    ? Math.Round((double)entry.MessagesCopied / entry.TotalMessages * 100, 1)
                    : 100;
                entry.LastUpdated = DateTime.UtcNow;
                batch.SyncedMailboxes++;

                _logger.LogInformation(
                    "MailboxMigrationWorker: completed mail copy for {SourceUpn} — {Copied}/{Total} messages{Gaps}.",
                    entry.SourceUpn, entry.MessagesCopied, entry.TotalMessages,
                    entry.ErrorMessage is null ? "" : " (with gaps: " + entry.ErrorMessage + ")");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "MailboxMigrationWorker: cancellation requested during copy of {SourceUpn}.",
                    entry.SourceUpn);
                break;
            }
            catch (Exception ex)
            {
                entry.Status = MailboxMigrationStatus.Failed;
                entry.ErrorMessage = ex.Message;
                entry.LastUpdated = DateTime.UtcNow;
                batch.FailedMailboxes++;

                _logger.LogWarning(ex,
                    "MailboxMigrationWorker: mail copy failed for {SourceUpn} (entry {EntryId}).",
                    entry.SourceUpn, entry.Id);
            }

            batch.LastSyncedAt = DateTime.UtcNow;
            await repo.SaveAsync(stoppingToken);
            await NotifyBatchProgressSafe(scope, batch, stoppingToken);
        }

        await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
    }

    // The idle sweep re-enqueues active batches every ~5s; EXO only needs to be polled
    // on a coarser cadence. Parked (Synced, awaiting cutover) batches poll slowest.
    private static readonly TimeSpan ActiveExoPollInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ParkedExoPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Native cross-tenant Mailbox Replication Service path. Each dequeue performs ONE
    /// unit of work and returns: either first-time setup (org relationships, endpoint,
    /// EXO batch creation) or a single status poll. Continuation is driven by the idle
    /// sweep re-enqueueing active batches, so one batch can never starve the queue and
    /// controller-side writes (Stop/Complete) are observed on the next dequeue.
    /// </summary>
    private async Task ProcessNativeMrsAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var batchId = batch.Id;
        var project = batch.Project!;
        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");

        if (isMock)
        {
            _logger.LogInformation(
                "MailboxMigrationWorker: batch {BatchId} (NativeMrs, mock) — simulating EXO move.", batchId);
            await SimulateNativeMrsAsync(scope, repo, batch, stoppingToken);
            return;
        }

        // Throttle polls of an existing EXO batch (setup is never throttled).
        if (!string.IsNullOrWhiteSpace(batch.ExoMigrationBatchId) && batch.LastSyncedAt is { } last)
        {
            var minInterval = batch.Status == BatchStatus.Synced
                ? ParkedExoPollInterval
                : ActiveExoPollInterval;
            if (DateTime.UtcNow - last < minInterval)
                return;
        }

        var sourceTenant = project.SourceTenant!;
        var targetTenant = project.TargetTenant!;
        if (string.IsNullOrWhiteSpace(sourceTenant.OnMicrosoftDomain) ||
            string.IsNullOrWhiteSpace(targetTenant.OnMicrosoftDomain))
        {
            await FailBatchAsync(scope, repo, batch,
                "Both source and target tenants must have OnMicrosoftDomain populated for native MRS. " +
                "Re-verify the tenant connections to auto-detect it.",
                stoppingToken);
            return;
        }

        var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
        var credentialFactory = scope.ServiceProvider.GetRequiredService<ITenantCredentialFactory>();
        var exo = scope.ServiceProvider.GetRequiredService<IExoRestClient>();

        Azure.Core.TokenCredential sourceCred, targetCred;
        try
        {
            var (srcCert, srcPw, srcSecret) = await keyVault.LoadCredentialsAsync(sourceTenant.Id, stoppingToken);
            sourceCred = credentialFactory.CreateCredential(sourceTenant, srcCert, srcPw, srcSecret);

            var (tgtCert, tgtPw, tgtSecret) = await keyVault.LoadCredentialsAsync(targetTenant.Id, stoppingToken);
            targetCred = credentialFactory.CreateCredential(targetTenant, tgtCert, tgtPw, tgtSecret);
        }
        catch (Exception ex)
        {
            await FailBatchAsync(scope, repo, batch,
                $"Failed to build EXO credentials: {ex.Message}", stoppingToken);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(batch.ExoMigrationBatchId))
            {
                await SetupNativeMrsBatchAsync(
                    scope, repo, exo, batch, sourceCred, targetCred, stoppingToken);
            }
            else
            {
                await PollNativeMrsOnceAsync(
                    scope, repo, exo, batch, targetTenant.TenantId, targetCred, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "MailboxMigrationWorker: NativeMrs processing cancelled for batch {BatchId} — will resume on next dequeue.",
                batchId);
        }
        catch (Exception ex)
        {
            await FailBatchAsync(scope, repo, batch,
                $"Native MRS setup failed: {ex.Message}", stoppingToken);
        }
    }

    /// <summary>
    /// First-time (or post-reset) setup: prep entries, ensure org relationships and the
    /// migration endpoint, then create + start the EXO migration batch. Runs once —
    /// subsequent dequeues take the poll path because <c>ExoMigrationBatchId</c> is set.
    /// </summary>
    private async Task SetupNativeMrsBatchAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        IExoRestClient exo,
        MailboxMigrationBatch batch,
        Azure.Core.TokenCredential sourceCred,
        Azure.Core.TokenCredential targetCred,
        CancellationToken stoppingToken)
    {
        var project = batch.Project!;
        var sourceTenant = project.SourceTenant!;
        var targetTenant = project.TargetTenant!;

        var sourcePartnerDomain = $"{sourceTenant.OnMicrosoftDomain}.onmicrosoft.com";
        var targetPartnerDomain = $"{targetTenant.OnMicrosoftDomain}.onmicrosoft.com";
        var targetDeliveryDomain = $"{targetTenant.OnMicrosoftDomain}.mail.onmicrosoft.com";

        // Cross-tenant Mailbox Migration app — Microsoft-published, fixed across tenants.
        // Override via Platform:CrossTenantMigration:AppId only if Microsoft publishes a new one.
        var crossTenantAppId = _configuration["Platform:CrossTenantMigration:AppId"]
            ?? "879f1d6d-c0b7-4543-a2dd-dfa812c5179d";
        // Secret resolves through the platform secret store (Key Vault when
        // enabled) — the config value may be a kv: marker, never used raw.
        var secretResolver = scope.ServiceProvider.GetRequiredService<IPlatformSecretResolver>();
        var crossTenantSecret = await secretResolver.GetAsync(
            "Platform:CrossTenantMigration:ClientSecret", stoppingToken);
        var scopeGroupName = $"CTMS-{targetTenant.OnMicrosoftDomain}";
        var scopeGroupSmtp = $"{scopeGroupName}@{sourcePartnerDomain}";

        // Pull the entries up-front so we can prep MailUsers + scope membership before
        // creating org relationships and the batch.
        var allEntries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var preppedUpns = await PrepareNativeMrsEntriesAsync(
            exo, repo, batch, allEntries,
            sourceTenant.TenantId, targetTenant.TenantId,
            scopeGroupName, scopeGroupSmtp, targetDeliveryDomain,
            sourceCred, targetCred, stoppingToken);

        var sourceUpns = preppedUpns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sourceUpns.Count == 0)
        {
            await FailBatchAsync(scope, repo, batch,
                "No mailboxes successfully prepared for native MRS — see per-entry errors.",
                stoppingToken);
            return;
        }

        // The migration batch CSV must list the TARGET MailUser addresses, not the
        // source ones. MRS onboarding resolves each CSV row to a recipient in the
        // TARGET org and requires it to be a MailUser (with the stamped ExchangeGuid
        // that tells MRS which source mailbox to pull). Passing the source address
        // makes MRS follow the MailUser's ExternalEmailAddress to the source
        // UserMailbox and fail: "The migration user type ... is not correct. Please
        // ensure it has RecipientTypeDetails:MailUser."
        var targetUpns = allEntries
            .Where(e => sourceUpns.Contains(e.SourceUpn, StringComparer.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.TargetUpn))
            .Select(e => e.TargetUpn!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Assign the Cross Tenant User Data Migration add-on license now that the
        // target MailUser stubs exist (PrepareNativeMrsEntriesAsync provisioned
        // them). Doing this at controller batch-start failed "User not found"
        // because the target object was not yet provisioned. Best-effort — never
        // blocks the move; NeedsApproval detection at poll time is the backstop.
        await AssignNativeMrsLicensesAsync(scope, batch, sourceTenant, targetTenant, sourceUpns, targetUpns, stoppingToken);

        await exo.EnsureOrganizationRelationshipAsync(
            sourceTenant.TenantId, targetPartnerDomain,
            $"CrossTenantMigration-{targetTenant.OnMicrosoftDomain}",
            "RemoteOutbound", sourceCred, stoppingToken,
            oauthApplicationId: crossTenantAppId,
            mailboxMovePublishedScopes: scopeGroupName,
            partnerTenantId: targetTenant.TenantId);

        await exo.EnsureOrganizationRelationshipAsync(
            targetTenant.TenantId, sourcePartnerDomain,
            $"CrossTenantMigration-{sourceTenant.OnMicrosoftDomain}",
            // Microsoft's cross-tenant script sets the TARGET side to "Inbound"
            // (not "RemoteInbound"); the source side is "RemoteOutbound".
            "Inbound", targetCred, stoppingToken,
            oauthApplicationId: crossTenantAppId,
            partnerTenantId: sourceTenant.TenantId);

        var (endpointId, _) = await exo.EnsureMigrationEndpointAsync(
            targetTenant.TenantId, sourcePartnerDomain, targetCred, stoppingToken,
            applicationId: crossTenantAppId,
            clientSecret: crossTenantSecret);

        // Include unix timestamp so retries get a fresh EXO batch name — avoids
        // MigrationJobAlreadyExistException when a prior attempt left a stuck batch
        // that EXO can't immediately remove (Remove-MigrationBatch returns 200 but
        // the soft-delete sticks for an unbounded period).
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exoBatchName = $"plat-{batch.Id:N}".Substring(0, Math.Min(22, $"plat-{batch.Id:N}".Length))
            + $"-{ts}";
        var creation = await exo.CreateMigrationBatchAsync(
            targetTenant.TenantId, exoBatchName, targetDeliveryDomain,
            endpointId, targetUpns, targetCred, stoppingToken);

        batch.ExoMigrationBatchId = creation.BatchId;

        foreach (var entry in allEntries)
        {
            if (entry.Status is MailboxMigrationStatus.Queued &&
                sourceUpns.Contains(entry.SourceUpn, StringComparer.OrdinalIgnoreCase))
            {
                entry.Status = MailboxMigrationStatus.Syncing;
            }
            entry.LastUpdated = DateTime.UtcNow;
        }

        await repo.SaveAsync(stoppingToken);
        await NotifyBatchProgressSafe(scope, batch, stoppingToken);

        _logger.LogInformation(
            "MailboxMigrationWorker: batch {BatchId} (NativeMrs) — EXO batch '{ExoBatchId}' created with {Count} mailbox(es).",
            batch.Id, creation.BatchId, sourceUpns.Count);
    }

    /// <summary>
    /// Assign the Cross Tenant User Data Migration add-on to the migrating users.
    /// Runs post-provisioning (target MailUser exists) so target-side assignment
    /// resolves. Gated by MailboxMigration:AutoAssignLicense (default true);
    /// LicenseAssignmentSide default "target"; DefaultUsageLocation default "US".
    /// Never throws — a missing SKU / exhausted seats / Graph error only warns;
    /// the per-user NeedsApproval detection at poll time is the backstop.
    /// </summary>
    private async Task AssignNativeMrsLicensesAsync(
        IServiceScope scope,
        MailboxMigrationBatch batch,
        Tenant sourceTenant,
        Tenant targetTenant,
        IReadOnlyList<string> sourceUpns,
        IReadOnlyList<string> targetUpns,
        CancellationToken ct)
    {
        if (_configuration.GetValue<bool>("Platform:MockGraphCalls")) return;
        if (!_configuration.GetValue("MailboxMigration:AutoAssignLicense", true)) return;

        var side = (_configuration.GetValue<string>("MailboxMigration:LicenseAssignmentSide") ?? "target")
            .Trim().ToLowerInvariant();
        var assignOnSource = LicenseAssignmentPolicy.AssignOnSource(side);
        var usageLocation = _configuration.GetValue<string>("MailboxMigration:DefaultUsageLocation") ?? "US";

        var upns = (assignOnSource ? sourceUpns : targetUpns)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (upns.Count == 0) return;

        try
        {
            var keyVault     = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
            var graphFactory = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();
            var licenseCheck = scope.ServiceProvider.GetRequiredService<ILicenseCheckService>();

            var tenant = assignOnSource ? sourceTenant : targetTenant;
            var (cert, pw, secret) = await keyVault.LoadCredentialsAsync(tenant.Id, ct);
            var graph = graphFactory.CreateForTenant(tenant, cert, pw, secret);

            var result = await licenseCheck.EnsureCrossTenantMigrationLicensesAsync(graph, upns, usageLocation, ct);

            // Target-side assignment races AAD read-after-write: the MailUser stub
            // was created seconds earlier and Graph assignLicense can 404 until
            // replication catches up (observed live 2026-07-07 and 2026-07-16).
            // Retry just the failed UPNs with backoff before accepting failure.
            foreach (var delaySeconds in new[] { 15, 30, 45 })
            {
                if (!result.SkuFound || result.Failed.Count == 0) break;

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                var retryUpns = result.Failed.Select(f => f.Upn).ToList();
                _logger.LogInformation(
                    "MailboxMigrationWorker: retrying license assignment for {Count} user(s) on batch {BatchId} after {Delay}s backoff.",
                    retryUpns.Count, batch.Id, delaySeconds);

                var retry = await licenseCheck.EnsureCrossTenantMigrationLicensesAsync(graph, retryUpns, usageLocation, ct);
                result = retry with
                {
                    Assigned          = result.Assigned.Concat(retry.Assigned).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    AlreadyLicensed   = result.AlreadyLicensed.Concat(retry.AlreadyLicensed).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    UsageLocationsSet = result.UsageLocationsSet.Concat(retry.UsageLocationsSet).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }

            if (!result.SkuFound)
                _logger.LogWarning(
                    "MailboxMigrationWorker: no 'Cross Tenant User Data Migration' SKU on the {Side} tenant for batch {BatchId} — " +
                    "purchase seats or assign manually, or moves will stall in NeedsApproval.",
                    side, batch.Id);
            foreach (var f in result.Failed)
                _logger.LogWarning(
                    "MailboxMigrationWorker: license assignment failed for {Upn} (batch {BatchId}): {Reason}",
                    f.Upn, batch.Id, f.Reason);

            _logger.LogInformation(
                "MailboxMigrationWorker: license auto-assign for batch {BatchId} ({Side} side): " +
                "{Assigned} assigned, {Already} already licensed, {Failed} failed ({Seats} seats available).",
                batch.Id, side, result.Assigned.Count, result.AlreadyLicensed.Count, result.Failed.Count, result.SeatsAvailable);

            var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
            await audit.AddAsync(new AuditEvent
            {
                Action    = "MAILBOX_LICENSES_AUTO_ASSIGNED",
                Resource  = $"projects/{batch.ProjectId}/mailbox-batches/{batch.Id}",
                Actor     = "system@platform",
                ProjectId = batch.ProjectId,
                Details   = $$$"""{"batchId":"{{{batch.Id}}}","side":"{{{side}}}","skuFound":{{{(result.SkuFound ? "true" : "false")}}},"assigned":{{{result.Assigned.Count}}},"alreadyLicensed":{{{result.AlreadyLicensed.Count}}},"failed":{{{result.Failed.Count}}}}""",
            }, ct);
            await audit.SaveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MailboxMigrationWorker: license auto-assign errored for batch {BatchId} — continuing (NeedsApproval is the backstop).",
                batch.Id);
        }
    }

    /// <summary>
    /// Per-user prep that must run before the cross-tenant batch is created:
    /// (1) ensure the scope DG exists on source and add each source UPN as a member,
    /// (2) capture each source mailbox's attributes,
    /// (3) provision a stamped MailUser stub on target.
    /// Entries that fail any step are marked <see cref="MailboxMigrationStatus.Failed"/>
    /// and excluded from the returned UPN list. The batch continues with the survivors.
    /// </summary>
    private async Task<List<string>> PrepareNativeMrsEntriesAsync(
        IExoRestClient exo,
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        IList<MailboxMigrationEntry> entries,
        string sourceAadTenantId,
        string targetAadTenantId,
        string scopeGroupName,
        string scopeGroupSmtp,
        string targetDeliveryDomain,
        Azure.Core.TokenCredential sourceCred,
        Azure.Core.TokenCredential targetCred,
        CancellationToken ct)
    {
        var preppedUpns = new List<string>();
        var pending = entries
            .Where(e => e.Status is MailboxMigrationStatus.Queued or MailboxMigrationStatus.Syncing)
            .Where(e => !string.IsNullOrWhiteSpace(e.SourceUpn) && !string.IsNullOrWhiteSpace(e.TargetUpn))
            .ToList();

        if (pending.Count == 0)
        {
            _logger.LogInformation(
                "MailboxMigrationWorker: batch {BatchId} — no entries need MRS prep (resuming).", batch.Id);
            return entries.Select(e => e.SourceUpn).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        }

        try
        {
            await exo.EnsureScopeDistributionGroupAsync(
                sourceAadTenantId, scopeGroupName, scopeGroupSmtp, sourceCred, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "MailboxMigrationWorker: failed to ensure scope DG '{Group}' on source for batch {BatchId} — " +
                "all entries will be marked Failed.", scopeGroupName, batch.Id);
            foreach (var entry in pending)
            {
                entry.Status = MailboxMigrationStatus.Failed;
                entry.ErrorMessage = $"Source scope group setup failed: {ex.Message}";
                entry.LastUpdated = DateTime.UtcNow;
            }
            await repo.SaveAsync(ct);
            return preppedUpns;
        }

        foreach (var entry in pending)
        {
            try
            {
                await exo.AddDistributionGroupMemberAsync(
                    sourceAadTenantId, scopeGroupName, entry.SourceUpn, sourceCred, ct);

                var attrs = await exo.GetMailboxAttributesAsync(
                    sourceAadTenantId, entry.SourceUpn, sourceCred, ct);
                if (attrs is null)
                    throw new InvalidOperationException(
                        $"Source mailbox not found for UPN '{entry.SourceUpn}'.");

                await exo.EnsureTargetMailUserAsync(
                    targetAadTenantId, entry.TargetUpn, attrs, targetDeliveryDomain, targetCred, ct);

                preppedUpns.Add(entry.SourceUpn);

                _logger.LogInformation(
                    "MailboxMigrationWorker: prepped {SourceUpn} → {TargetUpn} for native MRS (batch {BatchId}).",
                    entry.SourceUpn, entry.TargetUpn, batch.Id);
            }
            catch (Exception ex)
            {
                entry.Status = MailboxMigrationStatus.Failed;
                entry.ErrorMessage = $"Pre-batch prep failed: {ex.Message}";
                entry.LastUpdated = DateTime.UtcNow;
                _logger.LogWarning(ex,
                    "MailboxMigrationWorker: prep failed for {SourceUpn} (batch {BatchId}) — entry marked Failed.",
                    entry.SourceUpn, batch.Id);
            }
        }

        await repo.SaveAsync(ct);
        return preppedUpns;
    }

    /// <summary>
    /// One EXO status poll: reflects per-user state onto entries, advances the batch
    /// through Syncing → Synced (awaiting cutover) → Completing → terminal, and settles
    /// the batch when EXO reports a terminal state. Never loops — the idle sweep
    /// schedules the next poll.
    /// </summary>
    private async Task PollNativeMrsOnceAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        IExoRestClient exo,
        MailboxMigrationBatch batch,
        string targetAadTenantId,
        Azure.Core.TokenCredential targetCred,
        CancellationToken stoppingToken)
    {
        var exoBatchId = batch.ExoMigrationBatchId!;

        ExoBatchStatus? status;
        IReadOnlyList<ExoMigrationUser> users;
        try
        {
            status = await exo.GetMigrationBatchAsync(targetAadTenantId, exoBatchId, targetCred, stoppingToken);
            users  = await exo.GetMigrationUsersAsync(targetAadTenantId, exoBatchId, targetCred, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MailboxMigrationWorker: NativeMrs poll failed for batch {BatchId} — will retry on next sweep.",
                batch.Id);
            return;
        }

        if (status is null)
        {
            // The stored EXO batch was deleted (manual cleanup, MRS expiry, retry reset).
            // Drop the id — the next dequeue takes the setup path and recreates it.
            _logger.LogWarning(
                "MailboxMigrationWorker: stored EXO batch '{ExoBatchId}' not found for batch {BatchId} — will recreate.",
                exoBatchId, batch.Id);
            batch.ExoMigrationBatchId = null;
            await repo.SaveAsync(stoppingToken);
            return;
        }

        await UpdateEntriesFromExoUsersAsync(repo, batch, users, stoppingToken);
        batch.LastSyncedAt = DateTime.UtcNow;

        var exoStatus = status.Status?.Trim().ToLowerInvariant() ?? string.Empty;

        // Batch-level transition decided by a pure function (see NextBatchStatusFromExo)
        // so the mapping — including the "SyncedWithErrors with nothing synced is a
        // FAILURE, not awaiting-cutover" rule — is unit-testable in isolation.
        var nextStatus = NextBatchStatusFromExo(exoStatus, status.SyncedCount, status.FailedCount, batch.Status);
        if (nextStatus == BatchStatus.Failed)
        {
            batch.Status = BatchStatus.Failed;
            batch.CompletedAt = DateTime.UtcNow;
            batch.ErrorMessage =
                $"MRS initial sync failed for all {status.FailedCount} mailbox(es) — see per-entry errors " +
                "(run Get-MigrationUserStatistics -IncludeReport for full MRS detail).";
            _logger.LogWarning(
                "MailboxMigrationWorker: batch {BatchId} — EXO SyncedWithErrors, {Failed}/{Total} failed and " +
                "none synced — marking Failed (was masked as awaiting-cutover).",
                batch.Id, status.FailedCount, status.TotalCount);
        }
        else if (nextStatus == BatchStatus.Synced)
        {
            batch.Status = BatchStatus.Synced;
            _logger.LogInformation(
                "MailboxMigrationWorker: batch {BatchId} initial sync complete — parked awaiting cutover. " +
                "Trigger completion via POST /mailbox-batches/{BatchId}/complete.",
                batch.Id, batch.Id);
        }
        else if (nextStatus == BatchStatus.Completing)
        {
            // Completion may have been triggered outside the platform (EXO PowerShell/EAC).
            batch.Status = BatchStatus.Completing;
        }

        await repo.SaveAsync(stoppingToken);
        await NotifyBatchProgressSafe(scope, batch, stoppingToken);

        _logger.LogInformation(
            "MailboxMigrationWorker: NativeMrs batch {BatchId} — EXO status '{Status}' " +
            "(synced={Synced}, finalized={Finalized}, failed={Failed}, total={Total}).",
            batch.Id, status.Status, status.SyncedCount, status.FinalizedCount,
            status.FailedCount, status.TotalCount);

        if (IsExoTerminalStatus(status.Status ?? string.Empty))
        {
            // Close out any entries EXO left open so the finalizer can settle the batch.
            if (exoStatus is "failed" or "stopped" or "removed")
            {
                var entries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
                foreach (var e in entries.Where(e =>
                             e.Status is MailboxMigrationStatus.Queued or MailboxMigrationStatus.Syncing))
                {
                    e.Status = MailboxMigrationStatus.Failed;
                    e.ErrorMessage ??= $"EXO migration batch ended in status '{status.Status}'.";
                    e.LastUpdated = DateTime.UtcNow;
                }
                await repo.SaveAsync(stoppingToken);
            }
            await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
            return;
        }

        // Settle batches whose every entry individually failed (e.g. all NeedsApproval)
        // even though EXO still reports the batch as active — otherwise they poll forever.
        // Never settle while any entry is Synced: those are parked awaiting cutover.
        var current = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var anyOpen   = current.Any(e => e.Status is MailboxMigrationStatus.Queued or MailboxMigrationStatus.Syncing);
        var anySynced = current.Any(e => e.Status == MailboxMigrationStatus.Synced);
        if (!anyOpen && !anySynced)
            await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
    }

    private const string CrossTenantLicenseError =
        "EXO reports 'NeedsApproval' — the user is missing the 'Cross Tenant User Data Migration' " +
        "license. Assign it on either the source or target tenant (M365 admin center → Billing → " +
        "Licenses), then retry the batch.";

    private async Task UpdateEntriesFromExoUsersAsync(
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        IReadOnlyList<ExoMigrationUser> exoUsers,
        CancellationToken ct)
    {
        var entries = (await repo.GetEntriesByBatchAsync(batch.Id, ct)).ToList();

        // EXO can return duplicate rows for one address (e.g. a user re-added to the
        // batch); last row wins instead of ToDictionary throwing and failing the poll.
        var byUpn = new Dictionary<string, ExoMigrationUser>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in exoUsers)
        {
            if (string.IsNullOrWhiteSpace(u.EmailAddress)) continue;
            if (byUpn.ContainsKey(u.EmailAddress))
                _logger.LogWarning(
                    "MailboxMigrationWorker: EXO returned duplicate migration-user rows for '{Upn}' " +
                    "on batch {BatchId} — using the last row.", u.EmailAddress, batch.Id);
            byUpn[u.EmailAddress] = u;
        }

        foreach (var entry in entries)
        {
            // The EXO batch is created with TARGET MailUser addresses (the CSV fix),
            // so Get-MigrationUser rows key on the target UPN; fall back to the
            // source UPN for batches created before that fix.
            if (!byUpn.TryGetValue(entry.TargetUpn ?? string.Empty, out var u) &&
                !byUpn.TryGetValue(entry.SourceUpn, out u)) continue;

            var exoStatus = u.Status?.Trim().ToLowerInvariant();
            if (exoStatus == "needsapproval")
            {
                // Documented stall: without the Cross Tenant User Data Migration license the
                // move sits in NeedsApproval forever. Surface it as a failure with the fix.
                entry.Status = MailboxMigrationStatus.Failed;
                entry.ErrorMessage = string.IsNullOrWhiteSpace(u.Error)
                    ? CrossTenantLicenseError
                    : $"{CrossTenantLicenseError} EXO detail: {u.Error}";
            }
            else
            {
                var newStatus = MapExoUserStatus(u.Status);
                if (newStatus.HasValue) entry.Status = newStatus.Value;
                if (!string.IsNullOrWhiteSpace(u.Error)) entry.ErrorMessage = u.Error;
            }
            entry.LastUpdated = DateTime.UtcNow;

            if (entry.Status == MailboxMigrationStatus.Synced)
                entry.ItemsSyncedPercent = 100;
        }
    }

    /// <summary>
    /// Pure batch-level status transition from an EXO migration-batch poll.
    /// Returns the new <see cref="BatchStatus"/>, or null when no transition applies.
    /// Key rule: EXO "SyncedWithErrors" with nothing actually synced
    /// (SyncedCount==0, FailedCount&gt;0) is a FAILURE — not "awaiting cutover" —
    /// so it must not be parked at Synced (which would mask the failure and block retry).
    /// </summary>
    internal static BatchStatus? NextBatchStatusFromExo(
        string? exoStatus, int syncedCount, int failedCount, BatchStatus current)
    {
        var s = exoStatus?.Trim().ToLowerInvariant() ?? string.Empty;

        if (s == "syncedwitherrors" && syncedCount == 0 && failedCount > 0 &&
            current is BatchStatus.Syncing or BatchStatus.Synced)
            return BatchStatus.Failed;

        if (current == BatchStatus.Syncing &&
            s is "synced" or "syncedwitherrors" or "incrementalsyncing")
            return BatchStatus.Synced;

        if (current is BatchStatus.Syncing or BatchStatus.Synced &&
            s is "completing" or "completionsynced" or "completioninprogress")
            return BatchStatus.Completing;

        return null;
    }

    internal static MailboxMigrationStatus? MapExoUserStatus(string? exoStatus) =>
        exoStatus?.Trim().ToLowerInvariant() switch
        {
            "completed" or "synced" or "completedwithwarnings"
                => MailboxMigrationStatus.Synced,
            "failed" or "corruptdata" or "completionfailed"
                => MailboxMigrationStatus.Failed,
            "queued" or "provisioning" or "provisioned" or "stopped"
                => MailboxMigrationStatus.Queued,
            "syncing" or "synced_partial" or "incrementalsyncing"
                or "completing" or "completionsynced" or "completioninprogress"
                => MailboxMigrationStatus.Syncing,
            _ => null,
        };

    internal static bool IsExoTerminalStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "completed" or "completedwithwarnings" or "failed" or "stopped" or "removed"
                => true,
            _ => false,
        };

    /// <summary>
    /// Mock-mode native MRS: snap entries through Syncing → Synced quickly, then park the
    /// batch at Synced (awaiting cutover) to mirror the real MRS flow. When /complete
    /// moves the batch to Completing, the next dequeue finalizes it.
    /// </summary>
    private async Task SimulateNativeMrsAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        if (batch.Status == BatchStatus.Completing)
        {
            // Cutover was requested — settle the batch from current entry states.
            await FinalizeFromEntriesAsync(scope, repo, batch, stoppingToken);
            return;
        }

        if (batch.Status == BatchStatus.Synced)
            return; // already parked awaiting cutover

        if (string.IsNullOrWhiteSpace(batch.ExoMigrationBatchId))
            batch.ExoMigrationBatchId = $"mock-mrs-{batch.Id:N}".Substring(0, 24);

        var entries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        foreach (var entry in entries.Where(e => e.Status is MailboxMigrationStatus.Queued or MailboxMigrationStatus.Syncing))
        {
            entry.Status = MailboxMigrationStatus.Syncing;
            entry.LastUpdated = DateTime.UtcNow;
        }
        await repo.SaveAsync(stoppingToken);
        await NotifyBatchProgressSafe(scope, batch, stoppingToken);

        await Task.Delay(500, stoppingToken);

        foreach (var entry in entries.Where(e => e.Status == MailboxMigrationStatus.Syncing))
        {
            var synthetic = 80 + (Math.Abs(entry.SourceUpn.GetHashCode()) % 400);
            entry.TotalMessages = synthetic;
            entry.MessagesCopied = synthetic;
            entry.ItemsSyncedPercent = 100;
            entry.Status = MailboxMigrationStatus.Synced;
            entry.LastUpdated = DateTime.UtcNow;
        }
        batch.SyncedMailboxes = entries.Count(e => e.Status == MailboxMigrationStatus.Synced);
        batch.Status = BatchStatus.Synced;
        batch.LastSyncedAt = DateTime.UtcNow;
        await repo.SaveAsync(stoppingToken);
        await NotifyBatchProgressSafe(scope, batch, stoppingToken);

        _logger.LogInformation(
            "MailboxMigrationWorker (mock): batch {BatchId} parked at Synced — awaiting /complete.",
            batch.Id);
    }

    /// <summary>
    /// Recompute synced/failed counts from current entries and transition the batch
    /// to a terminal status. Shared by both Graph copy and native MRS finishers.
    /// </summary>
    private async Task FinalizeFromEntriesAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        CancellationToken stoppingToken)
    {
        var allEntries = (await repo.GetEntriesByBatchAsync(batch.Id, stoppingToken)).ToList();
        var anyOpen = allEntries.Any(e => e.Status is MailboxMigrationStatus.Queued or MailboxMigrationStatus.Syncing);
        if (anyOpen || stoppingToken.IsCancellationRequested) return;

        batch.SyncedMailboxes  = allEntries.Count(e => e.Status == MailboxMigrationStatus.Synced);
        batch.FailedMailboxes  = allEntries.Count(e => e.Status == MailboxMigrationStatus.Failed);
        batch.SkippedMailboxes = allEntries.Count(e => e.Status == MailboxMigrationStatus.Skipped);

        // Terminal status is decided against *attempted* mailboxes only (total − skipped).
        // Skipped entries don't count toward failure, so a batch with no real failures
        // lands in Completed even if every remaining entry was skipped.
        var attempted = batch.TotalMailboxes - batch.SkippedMailboxes;
        batch.Status = (attempted > 0 && batch.FailedMailboxes == attempted)
            ? BatchStatus.Failed
            : BatchStatus.Completed;
        batch.CompletedAt = DateTime.UtcNow;

        if (batch.FailedMailboxes > 0 && batch.SyncedMailboxes > 0)
            batch.ErrorMessage = $"{batch.FailedMailboxes} of {attempted} attempted mailbox(es) failed.";
        else if (batch.Status == BatchStatus.Completed && batch.SkippedMailboxes > 0 && batch.SyncedMailboxes == 0)
            batch.ErrorMessage = $"All {batch.SkippedMailboxes} mailbox(es) were skipped — nothing to migrate.";

        await repo.SaveAsync(stoppingToken);
        await NotifyAndAuditAsync(scope, batch, stoppingToken);

        _logger.LogInformation(
            "MailboxMigrationWorker: batch {BatchId} ({Strategy}) finished — {Status}. " +
            "Synced: {Synced}, Failed: {Failed}, Skipped: {Skipped}, Total: {Total}.",
            batch.Id, batch.Strategy, batch.Status,
            batch.SyncedMailboxes, batch.FailedMailboxes, batch.SkippedMailboxes, batch.TotalMailboxes);
    }

    private async Task FailBatchAsync(
        IServiceScope scope,
        IMailboxMigrationRepository repo,
        MailboxMigrationBatch batch,
        string error,
        CancellationToken ct)
    {
        _logger.LogWarning("MailboxMigrationWorker: batch {BatchId} marked Failed — {Error}", batch.Id, error);
        batch.Status = BatchStatus.Failed;
        batch.CompletedAt = DateTime.UtcNow;
        batch.ErrorMessage = error;
        await repo.SaveAsync(ct);
        await NotifyAndAuditAsync(scope, batch, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task NotifyAndAuditAsync(
        IServiceScope scope, MailboxMigrationBatch batch, CancellationToken ct)
    {
        await NotifyBatchProgressSafe(scope, batch, ct);
        await WriteTerminalAuditEventAsync(scope, batch, ct);
    }

    private async Task WriteTerminalAuditEventAsync(
        IServiceScope scope, MailboxMigrationBatch batch, CancellationToken ct)
    {
        try
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
            var action = batch.Status switch
            {
                BatchStatus.Completed => "MAILBOX_BATCH_COMPLETED",
                BatchStatus.Failed    => "MAILBOX_BATCH_FAILED",
                BatchStatus.Stopped   => "MAILBOX_BATCH_STOPPED",
                _                     => $"MAILBOX_BATCH_{batch.Status.ToString().ToUpperInvariant()}",
            };
            await audit.AddAsync(new AuditEvent
            {
                Action    = action,
                Resource  = $"projects/{batch.ProjectId}/mailbox-batches/{batch.Id}",
                Actor     = "system",
                ProjectId = batch.ProjectId,
                Outcome   = batch.Status == BatchStatus.Completed ? "success" : "failure",
                Details   = JsonSerializer.Serialize(new
                {
                    batchId = batch.Id,
                    synced = batch.SyncedMailboxes,
                    failed = batch.FailedMailboxes,
                    total = batch.TotalMailboxes,
                    error = batch.ErrorMessage,
                }),
            }, ct);
            await audit.SaveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "MailboxMigrationWorker: failed to write audit event for batch {BatchId} — ignoring.",
                batch.Id);
        }
    }

    private static async Task NotifyBatchProgressSafe(
        IServiceScope scope,
        MailboxMigrationBatch batch,
        CancellationToken ct)
    {
        try
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IProgressNotifier>();
            await notifier.NotifyMailboxBatchProgressAsync(
                batch.Id,
                batch.ProjectId,
                batch.SyncedMailboxes,
                batch.TotalMailboxes,
                batch.FailedMailboxes,
                batch.Status.ToCamelCase(),
                ct);
        }
        catch (Exception)
        {
            // SignalR failure must never crash the worker
        }
    }
}
