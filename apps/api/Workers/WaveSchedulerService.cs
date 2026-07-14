using Azure.Core;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services.Spo;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that automatically starts <see cref="MigrationWave"/>s whose
/// <see cref="MigrationWave.ScheduledStartAt"/> time has arrived.
///
/// Polls every 60 seconds, finds all Scheduled waves where <c>ScheduledStartAt &lt;= UtcNow</c>,
/// and starts each one by transitioning it to Running and enqueuing its Draft batches/jobs
/// into the existing <see cref="MailboxMigrationQueue"/> and <see cref="ContentMigrationQueue"/>.
///
/// The worker runs by default; set <c>Workers:Enabled=false</c> to disable all
/// background workers (e.g. on an API instance that should only serve HTTP).
/// </summary>
public sealed class WaveSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly MailboxMigrationQueue _mailboxQueue;
    private readonly ContentMigrationQueue _contentQueue;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WaveSchedulerService> _logger;

    public WaveSchedulerService(
        IServiceProvider services,
        MailboxMigrationQueue mailboxQueue,
        ContentMigrationQueue contentQueue,
        IConfiguration configuration,
        ILogger<WaveSchedulerService> logger)
    {
        _services     = services;
        _mailboxQueue = mailboxQueue;
        _contentQueue = contentQueue;
        _configuration = configuration;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Workers:Enabled", true))
        {
            _logger.LogWarning(
                "WaveSchedulerService: disabled via Workers:Enabled=false — scheduled waves will not auto-start.");
            return;
        }

        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "WaveSchedulerService: not the primary instance — scheduled waves suppressed.");
            return;
        }

        _logger.LogInformation("WaveSchedulerService started. Poll interval: {Interval}s.", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await ProcessDueWavesAsync(stoppingToken);
        }

        _logger.LogInformation("WaveSchedulerService stopped.");
    }

    private async Task ProcessDueWavesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var waves        = scope.ServiceProvider.GetRequiredService<IWaveRepository>();
            var batches      = scope.ServiceProvider.GetRequiredService<IMailboxMigrationRepository>();
            var contentJobs  = scope.ServiceProvider.GetRequiredService<IContentMigrationRepository>();
            var audit        = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
            var projectRepo  = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var keyVault     = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
            var credFactory  = scope.ServiceProvider.GetRequiredService<ITenantCredentialFactory>();
            var exoClient    = scope.ServiceProvider.GetRequiredService<IExoRestClient>();
            var spoClient    = scope.ServiceProvider.GetRequiredService<ISpoRestClient>();

            var dueWaves = (await waves.GetScheduledWavesDueAsync(DateTime.UtcNow, ct)).ToList();
            if (dueWaves.Count == 0) return;

            _logger.LogInformation(
                "WaveSchedulerService: {Count} wave(s) are due for auto-start.", dueWaves.Count);

            foreach (var wave in dueWaves)
            {
                await StartWaveAsync(
                    wave, batches, contentJobs, audit, waves,
                    projectRepo, keyVault, credFactory, exoClient, spoClient, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WaveSchedulerService: error during scheduled wave poll.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("WaveSchedulerService");
        }
    }

    private async Task StartWaveAsync(
        MigrationWave wave,
        IMailboxMigrationRepository batches,
        IContentMigrationRepository contentJobs,
        IAuditRepository audit,
        IWaveRepository waveRepo,
        IProjectRepository projectRepo,
        IKeyVaultCredentialService keyVault,
        ITenantCredentialFactory credFactory,
        IExoRestClient exoClient,
        ISpoRestClient spoClient,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "WaveSchedulerService: auto-starting wave {WaveId} ({Name}) for project {ProjectId}.",
            wave.Id, wave.Name, wave.ProjectId);

        var project = await projectRepo.GetByIdWithTenantsAsync(wave.ProjectId, ct);
        if (project?.SourceTenant is null || project?.TargetTenant is null)
        {
            _logger.LogWarning(
                "WaveSchedulerService: wave {WaveId} — source or target tenant not found for project {ProjectId}. Skipping.",
                wave.Id, wave.ProjectId);
            return;
        }

        var sourceTenant = project.SourceTenant;
        var targetTenant = project.TargetTenant;

        wave.Status    = WaveStatus.Running;
        wave.StartedAt = DateTime.UtcNow;

        var draftBatches = wave.MailboxBatches.Where(b => b.Status == BatchStatus.Draft).ToList();

        var candidateJobs = wave.ContentJobs.Where(j =>
            j.Status == ContentMigrationJobStatus.Draft ||
            j.Status == ContentMigrationJobStatus.Scheduled ||
            j.Status == ContentMigrationJobStatus.Ready).ToList();

        // OneDrive jobs must pass the Provisioning → Ready gate before content moves
        // can succeed (the target drives have to exist). Auto-starting a Draft
        // OneDrive job would bypass that gate, so skip it and tell the admin why.
        var draftJobs = new List<ContentMigrationJob>();
        foreach (var candidate in candidateJobs)
        {
            if (candidate.JobType == ContentMigrationJobType.OneDrive &&
                candidate.Status != ContentMigrationJobStatus.Ready)
            {
                _logger.LogWarning(
                    "WaveSchedulerService: wave {WaveId} — OneDrive job {JobId} is {Status}, not Ready. " +
                    "Skipping auto-start; run provisioning on the job first.",
                    wave.Id, candidate.Id, candidate.Status);
                candidate.ErrorMessage =
                    "Skipped by wave auto-start: OneDrive target drives are not provisioned. " +
                    "Run provisioning on this job (it must reach Ready), then start it.";
                continue;
            }
            draftJobs.Add(candidate);
        }

        // ── Start mailbox batches (uses TARGET tenant for EXO operations) ─────
        if (draftBatches.Count > 0)
        {
            string? exoEndpoint = null;
            string? targetDeliveryDomain = null;
            var targetPrefix = targetTenant.OnMicrosoftDomain;

            if (string.IsNullOrWhiteSpace(targetPrefix))
            {
                _logger.LogWarning(
                    "WaveSchedulerService: wave {WaveId} — target tenant OnMicrosoftDomain not set. Skipping mailbox batches.",
                    wave.Id);
            }
            else
            {
                targetDeliveryDomain = $"{targetPrefix}.mail.onmicrosoft.com";

                TokenCredential targetCredential;
                try
                {
                    var (kvCertBase64, kvCertPassword, kvSecret) = await keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
                    targetCredential = credFactory.CreateCredential(targetTenant, kvCertBase64, kvCertPassword, kvSecret);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "WaveSchedulerService: wave {WaveId} — failed to build target tenant credential. Skipping mailbox batches.",
                        wave.Id);
                    targetDeliveryDomain = null; // fall through with null endpoint
                    goto skipEndpointLookup;
                }

                try
                {
                    exoEndpoint = await exoClient.FindCrossTenantMigrationEndpointAsync(
                        targetTenant.TenantId, targetCredential, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "WaveSchedulerService: wave {WaveId} — failed to find EXO endpoint. Skipping mailbox batches.",
                        wave.Id);
                }

                skipEndpointLookup:;
            }

            foreach (var batch in draftBatches)
            {
                if (exoEndpoint is null || targetDeliveryDomain is null)
                {
                    batch.Status       = BatchStatus.Failed;
                    batch.ErrorMessage = "Wave auto-start could not find an EXO CrossTenantMigration endpoint in the target tenant.";
                    batch.CompletedAt  = DateTime.UtcNow;
                    continue;
                }

                try
                {
                    var entries    = await batches.GetEntriesByBatchAsync(batch.Id, ct);
                    var sourceUpns = entries.Select(e => e.SourceUpn).ToList();

                    TokenCredential batchCredential;
                    var (kvCert, kvCertPw, kvSec) = await keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
                    batchCredential = credFactory.CreateCredential(targetTenant, kvCert, kvCertPw, kvSec);

                    var result = await exoClient.CreateMigrationBatchAsync(
                        targetTenant.TenantId,
                        batch.Name,
                        targetDeliveryDomain,
                        exoEndpoint,
                        sourceUpns,
                        batchCredential,
                        ct);

                    batch.ExoMigrationBatchId = result.BatchId;
                    batch.Status    = BatchStatus.Syncing;
                    batch.StartedAt = DateTime.UtcNow;
                    _mailboxQueue.Channel.Writer.TryWrite(batch.Id);

                    _logger.LogInformation(
                        "WaveSchedulerService: wave {WaveId} — EXO batch created for batch {BatchId}. EXO ID: {ExoId}.",
                        wave.Id, batch.Id, result.BatchId);
                }
                catch (Exception ex)
                {
                    batch.Status       = BatchStatus.Failed;
                    batch.ErrorMessage = $"Wave auto-start failed to create EXO batch: {ex.Message}";
                    batch.CompletedAt  = DateTime.UtcNow;
                    _logger.LogWarning(ex,
                        "WaveSchedulerService: wave {WaveId} — failed to create EXO batch for {BatchId}.",
                        wave.Id, batch.Id);
                }
            }
        }
        await batches.SaveAsync(ct);

        // ── Start content jobs via SPO PowerShell (Start-SPOCrossTenantUserContentMove) ──
        if (draftJobs.Count > 0)
        {
            var sourceAdminUrl = $"https://{sourceTenant.OnMicrosoftDomain}-admin.sharepoint.com";
            var targetHostUrl  = $"https://{targetTenant.OnMicrosoftDomain}-my.sharepoint.com";

            Services.Spo.SpoPowerShellCredentials? spoCreds = null;
            var (srcCertB64, srcCertPw, _) = await keyVault.LoadCredentialsAsync(sourceTenant.Id, ct);
            if (string.IsNullOrEmpty(srcCertB64) ||
                string.IsNullOrWhiteSpace(sourceTenant.AppClientId) ||
                string.IsNullOrWhiteSpace(sourceTenant.TenantId))
            {
                _logger.LogWarning(
                    "WaveSchedulerService: wave {WaveId} — source tenant missing app-only cert / AppClientId / TenantId. Skipping content jobs.",
                    wave.Id);
                foreach (var job in draftJobs)
                {
                    job.Status       = ContentMigrationJobStatus.Failed;
                    job.ErrorMessage = "Source tenant is missing an app-only certificate or app registration info.";
                    job.CompletedAt  = DateTime.UtcNow;
                }
                goto skipContentJobs;
            }
            spoCreds = new Services.Spo.SpoPowerShellCredentials(
                sourceTenant.TenantId, sourceTenant.AppClientId, srcCertB64, srcCertPw);

            foreach (var job in draftJobs)
            {
                try
                {
                    var items     = (await contentJobs.GetItemsByJobAsync(job.Id, ct)).ToList();
                    var spoJobIds = new List<string>();

                    var isSharePoint = job.JobType == ContentMigrationJobType.SharePoint;

                    foreach (var item in items)
                    {
                        Services.Spo.SpoMigrationJobResult result;

                        if (isSharePoint)
                        {
                            if (string.IsNullOrWhiteSpace(item.SourceUrl) || string.IsNullOrWhiteSpace(item.TargetUrl))
                            {
                                item.Status       = ContentMigrationItemStatus.Failed;
                                item.ErrorMessage = "SharePoint item requires both SourceUrl and TargetUrl.";
                                item.LastUpdated  = DateTime.UtcNow;
                                continue;
                            }
                            result = await spoClient.StartSiteContentMoveAsync(
                                sourceAdminUrl, item.SourceUrl!, item.TargetUrl!, targetHostUrl, spoCreds, ct);
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(item.OwnerUpn) || string.IsNullOrWhiteSpace(item.TargetOwnerUpn))
                            {
                                item.Status       = ContentMigrationItemStatus.Failed;
                                item.ErrorMessage = "OneDrive item requires both OwnerUpn and TargetOwnerUpn.";
                                item.LastUpdated  = DateTime.UtcNow;
                                continue;
                            }
                            result = await spoClient.StartUserContentMoveAsync(
                                sourceAdminUrl, item.OwnerUpn!, item.TargetOwnerUpn!, targetHostUrl, spoCreds, ct);
                        }

                        spoJobIds.Add(result.JobId);
                        item.SpoJobId    = result.JobId;
                        item.Status      = ContentMigrationItemStatus.Running;
                        item.LastUpdated = DateTime.UtcNow;
                    }

                    if (spoJobIds.Count == 0)
                    {
                        job.Status       = ContentMigrationJobStatus.Failed;
                        job.ErrorMessage = "No items were queued; see per-item errors.";
                        job.CompletedAt  = DateTime.UtcNow;
                        continue;
                    }

                    job.SpoMigrationJobId = string.Join(',', spoJobIds);
                    job.Status    = ContentMigrationJobStatus.Running;
                    job.StartedAt = DateTime.UtcNow;
                    _contentQueue.Channel.Writer.TryWrite(job.Id);

                    _logger.LogInformation(
                        "WaveSchedulerService: wave {WaveId} — SPO jobs created for content job {JobId}. Count: {Count}.",
                        wave.Id, job.Id, spoJobIds.Count);
                }
                catch (Exception ex)
                {
                    job.Status       = ContentMigrationJobStatus.Failed;
                    job.ErrorMessage = $"Wave auto-start failed to create SPO job: {ex.Message}";
                    job.CompletedAt  = DateTime.UtcNow;
                    _logger.LogWarning(ex,
                        "WaveSchedulerService: wave {WaveId} — failed to create SPO job for {JobId}.",
                        wave.Id, job.Id);
                }
            }

            skipContentJobs:;
        }
        await contentJobs.SaveAsync(ct);

        await waveRepo.SaveAsync(ct);

        await audit.AddAsync(new AuditEvent
        {
            Action    = "WAVE_AUTO_STARTED",
            Resource  = $"projects/{wave.ProjectId}/waves/{wave.Id}",
            Actor     = "system",
            ProjectId = wave.ProjectId,
            Details   = $$$"""{"waveId":"{{{wave.Id}}}","scheduledStartAt":"{{{wave.ScheduledStartAt:O}}}"}""",
        }, ct);
        await audit.SaveAsync(ct);
    }
}
