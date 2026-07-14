using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Workers;

/// <summary>
/// Background service that drives the multi-phase domain cutover workflow.
///
/// Phases processed automatically: CleaningSource → RemovingDomain → WaitingForRelease
/// → (adds domain) → AwaitingDnsVerification (PAUSE) → VerifyingDomain → AssigningUsers
/// → AwaitingMxUpdate (PAUSE).
///
/// PAUSE phases require the admin to make DNS changes and click Continue in the UI.
/// </summary>
public class DomainCutoverWorker : BackgroundService
{
    private readonly DomainCutoverQueue _queue;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DomainCutoverWorker> _logger;

    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public DomainCutoverWorker(
        DomainCutoverQueue queue,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<DomainCutoverWorker> logger)
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
                "DomainCutoverWorker: disabled via Workers:Enabled=false — queued jobs will not run.");
            return;
        }

        if (!Services.InstanceLock.SingleInstanceState.IsPrimary)
        {
            _logger.LogWarning(
                "DomainCutoverWorker: not the primary instance — background processing suppressed.");
            return;
        }

        _logger.LogInformation("DomainCutoverWorker started.");

        // Re-hydrate active jobs
        await RehydrateAsync(stoppingToken);

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
                await PollActiveJobsAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                break; // host shutdown — exit quietly instead of failing the service
            }

            if (!hasItem) break;

            while (reader.TryRead(out var jobId))
            {
                _logger.LogInformation("DomainCutoverWorker: dequeued job {JobId}.", jobId);
                await ProcessJobAsync(jobId, stoppingToken);
            }
        }

        _logger.LogInformation("DomainCutoverWorker stopped.");
    }

    private async Task RehydrateAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDomainCutoverRepository>();
            foreach (var job in await repo.GetActiveJobsAsync(ct))
            {
                _logger.LogInformation("DomainCutoverWorker: re-hydrating job {JobId} ({Phase}).", job.Id, job.Phase);
                _queue.Channel.Writer.TryWrite(job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DomainCutoverWorker: re-hydration error.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("DomainCutoverWorker");
        }
    }

    private async Task PollActiveJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDomainCutoverRepository>();
            foreach (var job in await repo.GetActiveJobsAsync(ct))
                _queue.Channel.Writer.TryWrite(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DomainCutoverWorker: idle poll error.");
            _services.GetRequiredService<MigrationPlatform.Api.Services.Telemetry.PlatformMetrics>().RecordPollFailure("DomainCutoverWorker");
        }
    }

    // ── Phase dispatcher ─────────────────────────────────────────────────────

    private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDomainCutoverRepository>();
        var job = await repo.GetWithProjectAsync(jobId, ct);

        if (job is null) return;

        try
        {
            switch (job.Phase)
            {
                case DomainCutoverPhase.CleaningSource:
                    await PhaseCleanSourceAsync(scope, job, ct);
                    break;
                case DomainCutoverPhase.RemovingDomain:
                    await PhaseRemoveDomainAsync(scope, job, ct);
                    break;
                case DomainCutoverPhase.WaitingForRelease:
                    await PhaseWaitForReleaseAsync(scope, job, ct);
                    break;
                case DomainCutoverPhase.VerifyingDomain:
                    await PhaseVerifyDomainAsync(scope, job, ct);
                    break;
                case DomainCutoverPhase.AssigningUsers:
                    await PhaseAssignUsersAsync(scope, job, ct);
                    break;
                default:
                    // Pause phases (AwaitingDnsVerification, AwaitingMxUpdate) or terminal — skip
                    return;
            }
        }
        catch (Exception ex)
        {
            var failedPhase = job.Phase;
            _logger.LogError(ex, "DomainCutoverWorker: unhandled error in phase {Phase} for job {JobId}.", failedPhase, jobId);
            job.Phase = DomainCutoverPhase.Failed;
            job.ErrorMessage = $"Unhandled error in phase {failedPhase}: {ex.Message}";
            job.LastUpdatedAt = DateTime.UtcNow;
        }

        await repo.SaveAsync(ct);
    }

    // ── Phase: CleaningSource ────────────────────────────────────────────────

    private async Task PhaseCleanSourceAsync(IServiceScope scope, DomainCutoverJob job, CancellationToken ct)
    {
        _logger.LogInformation("DomainCutoverWorker: [{JobId}] Phase: CleaningSource — checking domain references.", job.Id);

        var (sourceClient, _) = await BuildGraphClientsAsync(scope, job, ct);
        var domainMgmt = scope.ServiceProvider.GetRequiredService<IDomainManagementClient>();

        var refCount = await domainMgmt.GetDomainReferenceCountAsync(sourceClient, job.DomainName, ct);

        _logger.LogInformation(
            "DomainCutoverWorker: [{JobId}] domain '{Domain}' has {Count} references in source tenant. " +
            "ForceDelete will auto-rename them.", job.Id, job.DomainName, refCount);

        // Advance to next phase — forceDelete handles the cleanup
        job.Phase = DomainCutoverPhase.RemovingDomain;
        job.LastUpdatedAt = DateTime.UtcNow;

        // Continue immediately
        _queue.Channel.Writer.TryWrite(job.Id);
    }

    // ── Phase: RemovingDomain ────────────────────────────────────────────────

    private async Task PhaseRemoveDomainAsync(IServiceScope scope, DomainCutoverJob job, CancellationToken ct)
    {
        _logger.LogInformation("DomainCutoverWorker: [{JobId}] Phase: RemovingDomain — force-deleting from source.", job.Id);

        var (sourceClient, _) = await BuildGraphClientsAsync(scope, job, ct);
        var domainMgmt = scope.ServiceProvider.GetRequiredService<IDomainManagementClient>();

        await domainMgmt.ForceDeleteDomainAsync(sourceClient, job.DomainName, ct);

        job.Phase = DomainCutoverPhase.WaitingForRelease;
        job.LastUpdatedAt = DateTime.UtcNow;

        // Wait a bit before trying to add to target
        await Task.Delay(RetryDelay, ct);
        _queue.Channel.Writer.TryWrite(job.Id);
    }

    // ── Phase: WaitingForRelease ─────────────────────────────────────────────

    private async Task PhaseWaitForReleaseAsync(IServiceScope scope, DomainCutoverJob job, CancellationToken ct)
    {
        _logger.LogInformation("DomainCutoverWorker: [{JobId}] Phase: WaitingForRelease — trying to add domain to target.", job.Id);

        var (_, targetClient) = await BuildGraphClientsAsync(scope, job, ct);
        var domainMgmt = scope.ServiceProvider.GetRequiredService<IDomainManagementClient>();

        try
        {
            await domainMgmt.AddDomainAsync(targetClient, job.DomainName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DomainCutoverWorker: [{JobId}] domain not yet released — will retry in {Delay}s.",
                job.Id, RetryDelay.TotalSeconds);
            job.LastUpdatedAt = DateTime.UtcNow;
            await Task.Delay(RetryDelay, ct);
            _queue.Channel.Writer.TryWrite(job.Id);
            return;
        }

        // Domain added — get verification records
        var txtRecord = await domainMgmt.GetVerificationTxtRecordAsync(targetClient, job.DomainName, ct);
        job.DnsVerificationRecord = txtRecord;

        // Derive target MX record
        job.TargetMxRecord = $"{job.DomainName.Replace('.', '-')}.mail.protection.outlook.com";

        // PAUSE — wait for admin to add DNS TXT record
        job.Phase = DomainCutoverPhase.AwaitingDnsVerification;
        job.LastUpdatedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "DomainCutoverWorker: [{JobId}] domain added to target. DNS TXT record needed: {TxtRecord}. " +
            "Waiting for admin to continue.", job.Id, txtRecord);
    }

    // ── Phase: VerifyingDomain ───────────────────────────────────────────────

    private async Task PhaseVerifyDomainAsync(IServiceScope scope, DomainCutoverJob job, CancellationToken ct)
    {
        _logger.LogInformation("DomainCutoverWorker: [{JobId}] Phase: VerifyingDomain — polling verification.", job.Id);

        var (_, targetClient) = await BuildGraphClientsAsync(scope, job, ct);
        var domainMgmt = scope.ServiceProvider.GetRequiredService<IDomainManagementClient>();

        var verified = await domainMgmt.VerifyDomainAsync(targetClient, job.DomainName, ct);

        if (!verified)
        {
            _logger.LogInformation(
                "DomainCutoverWorker: [{JobId}] domain not verified yet — retrying in {Delay}s.", job.Id, RetryDelay.TotalSeconds);
            job.LastUpdatedAt = DateTime.UtcNow;
            await Task.Delay(RetryDelay, ct);
            _queue.Channel.Writer.TryWrite(job.Id);
            return;
        }

        _logger.LogInformation("DomainCutoverWorker: [{JobId}] domain verified! Advancing to AssigningUsers.", job.Id);

        // Count target users that need the domain assigned
        var scanRepo = scope.ServiceProvider.GetRequiredService<IScanRepository>();
        var identityRepo = scope.ServiceProvider.GetRequiredService<IIdentityMapRepository>();
        var maps = await identityRepo.GetByProjectAsync(job.ProjectId, ct);
        var usersForDomain = maps
            .Where(m => m.SourceUpn.EndsWith($"@{job.DomainName}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        job.TotalUsers = usersForDomain.Count;
        job.Phase = DomainCutoverPhase.AssigningUsers;
        job.LastUpdatedAt = DateTime.UtcNow;

        _queue.Channel.Writer.TryWrite(job.Id);
    }

    // ── Phase: AssigningUsers ────────────────────────────────────────────────

    private async Task PhaseAssignUsersAsync(IServiceScope scope, DomainCutoverJob job, CancellationToken ct)
    {
        _logger.LogInformation(
            "DomainCutoverWorker: [{JobId}] Phase: AssigningUsers — updating UPNs and mailbox addresses for {Count} users.",
            job.Id, job.TotalUsers);

        var (_, targetClient) = await BuildGraphClientsAsync(scope, job, ct);
        var domainMgmt = scope.ServiceProvider.GetRequiredService<IDomainManagementClient>();
        var identityRepo = scope.ServiceProvider.GetRequiredService<IIdentityMapRepository>();

        var maps = (await identityRepo.GetByProjectAsync(job.ProjectId, ct))
            .Where(m => m.SourceUpn.EndsWith($"@{job.DomainName}", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(m.TargetUpn))
            .ToList();

        // Build EXO credential for the target tenant
        var targetTenant = job.Project?.TargetTenant;
        Azure.Core.TokenCredential? exoCredential = null;
        IExoRestClient? exoClient = null;

        if (targetTenant is not null)
        {
            try
            {
                var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();
                var credFactory = scope.ServiceProvider.GetRequiredService<ITenantCredentialFactory>();
                var (cert, pwd, secret) = await keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
                exoCredential = credFactory.CreateCredential(targetTenant, cert, pwd, secret);
                exoClient = scope.ServiceProvider.GetRequiredService<IExoRestClient>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DomainCutoverWorker: [{JobId}] failed to build EXO credential — mailbox SMTP update will be skipped.", job.Id);
            }
        }

        int completed = 0, failed = 0;

        foreach (var map in maps)
        {
            // Restore original UPN (e.g. jdoe@fabrikam.com)
            var newUpn = map.SourceUpn;
            // Current target UPN (e.g. jdoe@contoso.onmicrosoft.com)
            var currentTargetUpn = map.TargetUpn!;

            try
            {
                // Step 1: Update UPN via Graph (use current UPN as identifier)
                await domainMgmt.UpdateUserUpnAsync(targetClient, currentTargetUpn, newUpn, ct);
                _logger.LogInformation(
                    "DomainCutoverWorker: [{JobId}] updated UPN {OldUpn} → {NewUpn}.",
                    job.Id, currentTargetUpn, newUpn);

                // Step 2: Update primary SMTP via EXO (if credentials available)
                if (exoClient is not null && exoCredential is not null && targetTenant is not null)
                {
                    try
                    {
                        // Use the new UPN as identity since Graph UPN was just updated
                        await exoClient.SetMailboxPrimarySmtpAsync(
                            targetTenant.TenantId, newUpn, newUpn, exoCredential, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "DomainCutoverWorker: [{JobId}] Set-Mailbox failed for {Upn} — UPN was updated but SMTP may need manual fix.",
                            job.Id, newUpn);
                    }
                }

                completed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DomainCutoverWorker: [{JobId}] failed to assign domain to user {CurrentUpn} → {NewUpn}.",
                    job.Id, currentTargetUpn, newUpn);
                failed++;
            }

            job.CompletedUsers = completed;
            job.FailedUsers = failed;
            job.LastUpdatedAt = DateTime.UtcNow;
        }

        _logger.LogInformation(
            "DomainCutoverWorker: [{JobId}] user assignment complete. Completed: {Completed}, Failed: {Failed}.",
            job.Id, completed, failed);

        // PAUSE for MX/DNS update
        job.Phase = DomainCutoverPhase.AwaitingMxUpdate;
        job.LastUpdatedAt = DateTime.UtcNow;

        if (failed > 0)
            job.ErrorMessage = $"{failed} user(s) failed during domain assignment. Check logs for details.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Microsoft.Graph.GraphServiceClient source, Microsoft.Graph.GraphServiceClient target)> BuildGraphClientsAsync(
        IServiceScope scope, DomainCutoverJob job, CancellationToken ct)
    {
        var graphFactory = scope.ServiceProvider.GetRequiredService<IGraphClientFactory>();
        var keyVault = scope.ServiceProvider.GetRequiredService<IKeyVaultCredentialService>();

        var sourceTenant = job.Project?.SourceTenant
            ?? throw new InvalidOperationException($"Job {job.Id}: source tenant not loaded.");
        var targetTenant = job.Project?.TargetTenant
            ?? throw new InvalidOperationException($"Job {job.Id}: target tenant not loaded.");

        // Load credentials from Key Vault and create clients
        var (srcCert, srcPwd, srcSecret) = await keyVault.LoadCredentialsAsync(sourceTenant.Id, ct);
        var (tgtCert, tgtPwd, tgtSecret) = await keyVault.LoadCredentialsAsync(targetTenant.Id, ct);

        var sourceClient = graphFactory.CreateForTenant(sourceTenant, srcCert, srcPwd, srcSecret);
        var targetClient = graphFactory.CreateForTenant(targetTenant, tgtCert, tgtPwd, tgtSecret);

        return (sourceClient, targetClient);
    }
}
