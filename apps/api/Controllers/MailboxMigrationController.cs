using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Orchestrates mailbox migration batches for a project using Microsoft Graph
/// to copy mail content from source to target tenant. Each entry (user) is
/// processed sequentially by the background worker.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/mailbox-batches")]
[Authorize]
public class MailboxMigrationController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IMailboxMigrationRepository _batches;
    private readonly IAuditRepository _audit;
    private readonly MailboxMigrationQueue _queue;
    private readonly IGraphClientFactory _graphFactory;
    private readonly ILicenseCheckService _licenseCheck;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly ITenantCredentialFactory _credentialFactory;
    private readonly IExoRestClient _exo;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<MailboxMigrationController> _logger;

    public MailboxMigrationController(
        IProjectRepository projects,
        IMailboxMigrationRepository batches,
        IAuditRepository audit,
        MailboxMigrationQueue queue,
        IGraphClientFactory graphFactory,
        ILicenseCheckService licenseCheck,
        IKeyVaultCredentialService keyVault,
        ITenantCredentialFactory credentialFactory,
        IExoRestClient exo,
        IConfiguration configuration,
        ICurrentUserService currentUser,
        ILogger<MailboxMigrationController> logger)
    {
        _projects = projects;
        _batches = batches;
        _audit = audit;
        _queue = queue;
        _graphFactory = graphFactory;
        _licenseCheck = licenseCheck;
        _keyVault = keyVault;
        _credentialFactory = credentialFactory;
        _exo = exo;
        _configuration = configuration;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>List all mailbox migration batches for the given project.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var results = await _batches.GetBatchesByProjectAsync(projectId, ct);
        return Ok(results.Select(MapToResponse));
    }

    /// <summary>
    /// Create a new mailbox migration batch in Draft status.
    /// Optionally specify <c>targetFolderName</c> to place all copied mail
    /// under a specific folder in each target mailbox.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateMailboxBatchRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Batch name is required.");

        if (req.Mailboxes is null || req.Mailboxes.Count == 0)
            return BadRequest("At least one mailbox entry is required.");

        var invalid = req.Mailboxes
            .Where(m => string.IsNullOrWhiteSpace(m.SourceUpn) || string.IsNullOrWhiteSpace(m.TargetUpn))
            .ToList();
        if (invalid.Count > 0)
            return BadRequest("All mailbox entries must have both SourceUpn and TargetUpn.");

        var strategy = ParseStrategy(req.Strategy);

        var batch = new MailboxMigrationBatch
        {
            ProjectId        = projectId,
            Name             = req.Name.Trim(),
            Status           = BatchStatus.Draft,
            Strategy         = strategy,
            TotalMailboxes   = req.Mailboxes.Count,
            TargetFolderName = strategy == MailboxMigrationStrategy.NativeMrs
                ? null
                : req.TargetFolderName?.Trim(),
        };

        var entries = req.Mailboxes.Select(m => new MailboxMigrationEntry
        {
            BatchId   = batch.Id,
            SourceUpn = m.SourceUpn.Trim(),
            TargetUpn = m.TargetUpn.Trim(),
            Status    = MailboxMigrationStatus.Queued,
        }).ToList();

        await _batches.AddBatchAsync(batch, ct);
        await _batches.AddEntriesAsync(entries, ct);
        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_CREATED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batch.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batch.Id}}}","name":"{{{batch.Name}}}","strategy":"{{{batch.Strategy.ToCamelCase()}}}","mailboxCount":{{{batch.TotalMailboxes}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Mailbox migration batch {BatchId} ({Name}) created for project {ProjectId} with {Count} mailboxes.",
            batch.Id, batch.Name, projectId, batch.TotalMailboxes);

        return CreatedAtAction(
            nameof(GetById),
            new { projectId, batchId = batch.Id },
            MapToResponse(batch));
    }

    /// <summary>Get a single mailbox migration batch by ID.</summary>
    [HttpGet("{batchId:guid}")]
    public async Task<IActionResult> GetById(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        return Ok(MapToResponse(batch));
    }

    /// <summary>List all mailbox entries within a batch.</summary>
    [HttpGet("{batchId:guid}/entries")]
    public async Task<IActionResult> GetEntries(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        var entries = await _batches.GetEntriesByBatchAsync(batchId, ct);
        return Ok(entries.Select(MapEntryToResponse));
    }

    /// <summary>
    /// Start a Draft batch — validates Graph credentials for both tenants,
    /// transitions to Syncing, and enqueues for the background worker.
    /// The worker copies mail one user at a time via Microsoft Graph.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/start")]
    public async Task<IActionResult> Start(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Status != BatchStatus.Draft)
            return BadRequest($"Only Draft batches can be started. Current status: {batch.Status}.");

        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");

        var project = await _projects.GetByIdWithTenantsAsync(batch.ProjectId, ct);
        if (project?.SourceTenant is null || project.TargetTenant is null)
            return UnprocessableEntity(new { message = "Project must have both source and target tenants configured." });

        LicenseAssignmentSummary? licenseSummary = null;

        if (!isMock)
        {
            // Validate Graph credentials for both tenants (skipped in mock mode).
            // The source client is built only to validate its credentials load; the
            // target client is used by the GraphCopy Exchange-license precheck below.
            GraphServiceClient targetGraph;
            try
            {
                var (srcCert, srcPw, srcSecret) = await _keyVault.LoadCredentialsAsync(project.SourceTenant.Id, ct);
                _ = _graphFactory.CreateForTenant(project.SourceTenant, srcCert, srcPw, srcSecret);

                var (tgtCert, tgtPw, tgtSecret) = await _keyVault.LoadCredentialsAsync(project.TargetTenant.Id, ct);
                targetGraph = _graphFactory.CreateForTenant(project.TargetTenant, tgtCert, tgtPw, tgtSecret);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { message = $"Credential validation failed: {ex.Message}" });
            }

            // Verify every target user has an active Exchange Online plan. Only required
            // for GraphCopy — native MRS expects MailUser stubs at start, with licenses
            // assigned at the cutover step.
            if (batch.Strategy == MailboxMigrationStrategy.GraphCopy)
            {
                try
                {
                    var entries = await _batches.GetEntriesByBatchAsync(batchId, ct);
                    var upns = entries
                        .Where(e => !string.IsNullOrWhiteSpace(e.TargetUpn))
                        .Select(e => e.TargetUpn)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (upns.Count > 0)
                    {
                        var verdicts = await _licenseCheck.CheckExchangeLicensesAsync(targetGraph, upns, ct);
                        var unlicensed = verdicts.Where(v => !v.HasLicense).ToList();
                        if (unlicensed.Count > 0)
                        {
                            _logger.LogWarning(
                                "MailboxMigrationController: {Count} target user(s) on batch {BatchId} are missing an Exchange Online license.",
                                unlicensed.Count, batchId);
                            return UnprocessableEntity(new
                            {
                                message = $"{unlicensed.Count} target user(s) are missing an active Exchange Online license. Assign an Exchange-bearing license (e.g. Microsoft 365 E3, Business Standard, Exchange Online Plan 1) and try again.",
                                unlicensedUsers = unlicensed.Select(u => new { upn = u.Upn, reason = u.Reason }),
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MailboxMigrationController: license check failed for batch {BatchId} — skipping precheck.", batchId);
                }
            }

            // Native MRS: the Cross Tenant User Data Migration add-on license
            // (CLAUDE.md prereq #6) is assigned by MailboxMigrationWorker AFTER it
            // provisions the target MailUser stub — assigning it here at batch start
            // fails "User not found" because the target object does not exist yet.
            // See AssignNativeMrsLicensesAsync in the worker.
        }
        else
        {
            _logger.LogInformation(
                "MailboxMigrationController: mock mode — skipping credential validation and license auto-assign for batch {BatchId}.",
                batchId);
        }

        batch.Status = BatchStatus.Syncing;
        batch.StartedAt = DateTime.UtcNow;
        await _batches.SaveAsync(ct);

        _queue.Channel.Writer.TryWrite(batchId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_STARTED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","targetFolder":"{{{batch.TargetFolderName ?? "(default)"}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Mailbox batch {BatchId} started for project {ProjectId}. Target folder: {TargetFolder}.",
            batchId, projectId, batch.TargetFolderName ?? "(default)");

        return Ok(MapToResponse(batch) with { LicenseAssignment = licenseSummary });
    }

    /// <summary>
    /// Trigger the final cutover for a native-MRS batch that has finished its initial
    /// sync (batch status <c>Synced</c>). Calls <c>Complete-MigrationBatch</c> on EXO,
    /// transitions the batch to <c>Completing</c>, and re-enqueues it so the worker polls
    /// completion through to a terminal state.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Strategy != MailboxMigrationStrategy.NativeMrs)
            return UnprocessableEntity(new
            {
                message = "Only native MRS batches have a cutover step. Graph-copy batches complete automatically."
            });

        if (batch.Status != BatchStatus.Synced)
            return UnprocessableEntity(new
            {
                message = $"Only batches in Synced (awaiting cutover) state can be completed. " +
                          $"Current status: {batch.Status}."
            });

        if (string.IsNullOrWhiteSpace(batch.ExoMigrationBatchId))
            return UnprocessableEntity(new { message = "Batch has no EXO migration batch to complete." });

        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");
        if (!isMock)
        {
            var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
            if (project?.TargetTenant is null)
                return UnprocessableEntity(new { message = "Project must have a target tenant configured." });

            try
            {
                var (tgtCert, tgtPw, tgtSecret) = await _keyVault.LoadCredentialsAsync(project.TargetTenant.Id, ct);
                var targetCred = _credentialFactory.CreateCredential(project.TargetTenant, tgtCert, tgtPw, tgtSecret);
                await _exo.CompleteMigrationBatchAsync(
                    project.TargetTenant.TenantId, batch.ExoMigrationBatchId, targetCred, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MailboxMigrationController: Complete-MigrationBatch failed for batch {BatchId}.", batchId);
                return UnprocessableEntity(new { message = $"EXO completion failed: {ex.Message}" });
            }
        }

        batch.Status = BatchStatus.Completing;
        await _batches.SaveAsync(ct);

        _queue.Channel.Writer.TryWrite(batchId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_COMPLETION_STARTED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","exoBatchId":"{{{batch.ExoMigrationBatchId}}}","mock":{{{(isMock ? "true" : "false")}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Mailbox batch {BatchId} cutover triggered for project {ProjectId} (EXO batch '{ExoBatchId}').",
            batchId, projectId, batch.ExoMigrationBatchId);

        return Ok(MapToResponse(batch));
    }

    /// <summary>
    /// Reset a Failed or Stopped batch to Draft and clear per-entry Failed status
    /// (Failed → Queued; preserves already-Synced and Skipped entries). For native-MRS
    /// batches the stale EXO migration batch and any stuck MoveRequests for the reset
    /// entries are removed best-effort and the stored EXO batch id is cleared, so the
    /// next <c>POST /start</c> creates a fresh EXO batch instead of "resuming" a
    /// terminal one and instantly re-failing the same entries.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Status is not (BatchStatus.Failed or BatchStatus.Stopped))
            return BadRequest($"Only Failed or Stopped batches can be retried. Current status: {batch.Status}.");

        var entries = (await _batches.GetEntriesByBatchAsync(batchId, ct)).ToList();
        var failedEntries = entries.Where(e => e.Status == MailboxMigrationStatus.Failed).ToList();

        // Native MRS: clear stale EXO state so the retry creates a fresh EXO batch.
        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");
        var retryWarnings = new List<string>();
        if (batch.Strategy == MailboxMigrationStrategy.NativeMrs &&
            !string.IsNullOrWhiteSpace(batch.ExoMigrationBatchId))
        {
            if (!isMock)
            {
                var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
                if (project?.TargetTenant is not null)
                {
                    try
                    {
                        var (tgtCert, tgtPw, tgtSecret) = await _keyVault.LoadCredentialsAsync(project.TargetTenant.Id, ct);
                        var targetCred = _credentialFactory.CreateCredential(project.TargetTenant, tgtCert, tgtPw, tgtSecret);
                        var targetTenantId = project.TargetTenant.TenantId;

                        try
                        {
                            await _exo.RemoveMigrationBatchAsync(
                                targetTenantId, batch.ExoMigrationBatchId, targetCred, ct);
                        }
                        catch (Exception ex)
                        {
                            retryWarnings.Add($"Remove-MigrationBatch({batch.ExoMigrationBatchId}): {ex.Message}");
                        }

                        // Stuck MoveRequests block the next batch from picking these users up.
                        foreach (var e in failedEntries.Where(e => !string.IsNullOrWhiteSpace(e.TargetUpn)))
                        {
                            try
                            {
                                await _exo.RemoveMoveRequestAsync(targetTenantId, e.TargetUpn, targetCred, ct);
                            }
                            catch (Exception ex)
                            {
                                retryWarnings.Add($"Remove-MoveRequest({e.TargetUpn}): {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryWarnings.Add($"EXO credential setup: {ex.Message}");
                    }
                }
            }
            batch.ExoMigrationBatchId = null;

            if (retryWarnings.Count > 0)
                _logger.LogWarning(
                    "MailboxMigrationController: retry cleanup for batch {BatchId} had {Count} warning(s): {Warnings}",
                    batchId, retryWarnings.Count, string.Join(" | ", retryWarnings));
        }

        var resetCount = 0;
        foreach (var e in failedEntries)
        {
            e.Status = MailboxMigrationStatus.Queued;
            e.ErrorMessage = null;
            e.ItemsSyncedPercent = 0;
            e.MessagesCopied = 0;
            e.LastUpdated = DateTime.UtcNow;
            resetCount++;
        }

        batch.Status = BatchStatus.Draft;
        batch.ErrorMessage = null;
        batch.StartedAt = null;
        batch.CompletedAt = null;
        batch.SyncedMailboxes  = entries.Count(e => e.Status == MailboxMigrationStatus.Synced);
        batch.FailedMailboxes  = entries.Count(e => e.Status == MailboxMigrationStatus.Failed);
        batch.SkippedMailboxes = entries.Count(e => e.Status == MailboxMigrationStatus.Skipped);

        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_RETRIED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","resetEntries":{{{resetCount}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Mailbox batch {BatchId} reset for retry on project {ProjectId}; {Count} entries returned to Queued.",
            batchId, projectId, resetCount);

        return Ok(MapToResponse(batch));
    }

    /// <summary>
    /// Wipe target-tenant EXO state created by prior attempts of this batch and reset
    /// the platform record to <c>Draft</c> so a fresh retry has a clean slate. In order:
    /// (1) <c>Remove-MoveRequest</c> per target UPN, (2) <c>Remove-MigrationBatch</c>
    /// for the stored <c>ExoMigrationBatchId</c>, (3) <c>Remove-MailUser</c> per target
    /// UPN, (4) <c>Remove-MailUser -PermanentlyDelete</c> for any soft-deleted MailUsers
    /// whose <c>ExternalEmailAddress</c> matches one of this batch's source UPNs, then
    /// reset entries to <c>Queued</c> and the batch to <c>Draft</c>.
    /// Refuses while the batch is <c>Syncing</c> or <c>Completing</c> — stop it first.
    /// In mock mode (<c>Platform:MockGraphCalls=true</c>) only the platform-side reset runs.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/reset-target")]
    public async Task<IActionResult> ResetTarget(
        Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Status is BatchStatus.Syncing or BatchStatus.Synced or BatchStatus.Completing)
            return UnprocessableEntity(new
            {
                message = "Cannot reset an active batch. Stop the batch first."
            });

        var entries = (await _batches.GetEntriesByBatchAsync(batchId, ct)).ToList();
        var warnings = new List<string>();
        var moveRequestsRemoved = 0;
        var mailUsersRemoved = 0;
        var softDeletedPurged = 0;
        var exoBatchRemoved = false;

        var isMock = _configuration.GetValue<bool>("Platform:MockGraphCalls");

        if (!isMock)
        {
            var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
            if (project?.SourceTenant is null || project.TargetTenant is null)
                return UnprocessableEntity(new
                {
                    message = "Project must have both source and target tenants configured."
                });

            Azure.Core.TokenCredential targetCred;
            try
            {
                var (tgtCert, tgtPw, tgtSecret) = await _keyVault.LoadCredentialsAsync(project.TargetTenant.Id, ct);
                targetCred = _credentialFactory.CreateCredential(project.TargetTenant, tgtCert, tgtPw, tgtSecret);
            }
            catch (Exception ex)
            {
                return UnprocessableEntity(new
                {
                    message = $"Failed to build target EXO credentials: {ex.Message}"
                });
            }

            var targetTenantId = project.TargetTenant.TenantId;
            var targetUpns = entries
                .Select(e => e.TargetUpn)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sourceUpns = entries
                .Select(e => e.SourceUpn)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // (1) Drop any in-flight MoveRequest per target UPN.
            foreach (var upn in targetUpns)
            {
                try
                {
                    await _exo.RemoveMoveRequestAsync(targetTenantId, upn, targetCred, ct);
                    moveRequestsRemoved++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Remove-MoveRequest({upn}): {ex.Message}");
                }
            }

            // (2) Drop the stored EXO migration batch (if any).
            if (!string.IsNullOrWhiteSpace(batch.ExoMigrationBatchId))
            {
                try
                {
                    await _exo.RemoveMigrationBatchAsync(
                        targetTenantId, batch.ExoMigrationBatchId, targetCred, ct);
                    exoBatchRemoved = true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Remove-MigrationBatch({batch.ExoMigrationBatchId}): {ex.Message}");
                }
            }

            // (3) Soft-delete each provisioned target MailUser stub.
            foreach (var upn in targetUpns)
            {
                try
                {
                    await _exo.RemoveMailUserAsync(targetTenantId, upn, targetCred, ct);
                    mailUsersRemoved++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Remove-MailUser({upn}): {ex.Message}");
                }
            }

            // (4) Permanently purge any soft-deleted MailUsers carrying our exact source UPNs.
            //     Scoping to source-UPN matches (not source-domain wildcards) keeps the purge
            //     surgical — only stubs left over from a prior attempt at THIS batch are removed.
            try
            {
                var softDeletedIds = await _exo.GetSoftDeletedMailUsersByExternalEmailAsync(
                    targetTenantId, sourceUpns, targetCred, ct);
                foreach (var id in softDeletedIds)
                {
                    try
                    {
                        await _exo.PurgeSoftDeletedMailUserAsync(targetTenantId, id, targetCred, ct);
                        softDeletedPurged++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Remove-MailUser -PermanentlyDelete({id}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Get-MailUser -SoftDeletedMailUser: {ex.Message}");
            }
        }
        else
        {
            _logger.LogInformation(
                "MailboxMigrationController: mock mode — skipping EXO cleanup for batch {BatchId}.",
                batchId);
        }

        // Platform-side reset (always runs).
        var entriesReset = 0;
        foreach (var e in entries)
        {
            if (e.Status is MailboxMigrationStatus.Queued) continue;
            e.Status = MailboxMigrationStatus.Queued;
            e.ErrorMessage = null;
            e.ItemsSyncedPercent = 0;
            e.MessagesCopied = 0;
            e.LastUpdated = DateTime.UtcNow;
            entriesReset++;
        }

        batch.Status = BatchStatus.Draft;
        batch.ErrorMessage = null;
        batch.StartedAt = null;
        batch.CompletedAt = null;
        batch.LastSyncedAt = null;
        batch.ExoMigrationBatchId = null;
        batch.SyncedMailboxes = 0;
        batch.FailedMailboxes = 0;
        batch.SkippedMailboxes = 0;

        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_TARGET_RESET",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","mock":{{{(isMock ? "true" : "false")}}},"moveRequestsRemoved":{{{moveRequestsRemoved}}},"mailUsersRemoved":{{{mailUsersRemoved}}},"softDeletedPurged":{{{softDeletedPurged}}},"exoBatchRemoved":{{{(exoBatchRemoved ? "true" : "false")}}},"entriesReset":{{{entriesReset}}},"warningCount":{{{warnings.Count}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Mailbox batch {BatchId} target reset (mock={Mock}): MoveReq×{MR}, MailUsers×{MU}, SoftDeletedPurged×{SD}, ExoBatchRemoved={EBR}, entriesReset={ER}, warnings={WC}.",
            batchId, isMock, moveRequestsRemoved, mailUsersRemoved, softDeletedPurged, exoBatchRemoved, entriesReset, warnings.Count);

        return Ok(new ResetMailboxBatchTargetResponse(
            BatchId:                    batchId,
            MoveRequestsRemoved:        moveRequestsRemoved,
            MailUsersRemoved:           mailUsersRemoved,
            SoftDeletedMailUsersPurged: softDeletedPurged,
            ExoBatchRemoved:            exoBatchRemoved,
            EntriesReset:               entriesReset,
            Warnings:                   warnings,
            Batch:                      MapToResponse(batch)
        ));
    }

    /// <summary>Stop a Syncing or Synced (awaiting cutover) batch — transitions it to Stopped.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/stop")]
    public async Task<IActionResult> Stop(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Status is not (BatchStatus.Syncing or BatchStatus.Synced))
            return BadRequest($"Only Syncing or Synced batches can be stopped. Current status: {batch.Status}.");

        batch.Status = BatchStatus.Stopped;
        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_STOPPED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("Mailbox batch {BatchId} stopped for project {ProjectId}.", batchId, projectId);
        return Ok(MapToResponse(batch));
    }

    /// <summary>
    /// Reclassify a single entry as <c>Skipped</c>. Useful when an entry failed for
    /// reasons outside the migration itself (e.g. target mailbox doesn't exist yet,
    /// target UPN is a stub) and the user wants the batch status to reflect only
    /// mailboxes that were actually migratable.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/entries/{entryId:guid}/skip")]
    public async Task<IActionResult> SkipEntry(
        Guid projectId, Guid batchId, Guid entryId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        var entries = (await _batches.GetEntriesByBatchAsync(batchId, ct)).ToList();
        var entry = entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null)
            return NotFound($"Entry {entryId} not found in batch {batchId}.");

        if (entry.Status == MailboxMigrationStatus.Skipped)
            return Ok(MapEntryToResponse(entry));

        var previousStatus = entry.Status;
        entry.Status = MailboxMigrationStatus.Skipped;
        entry.LastUpdated = DateTime.UtcNow;

        // Recompute batch counters + terminal status against attempted-only total.
        batch.SyncedMailboxes  = entries.Count(e => e.Status == MailboxMigrationStatus.Synced);
        batch.FailedMailboxes  = entries.Count(e => e.Status == MailboxMigrationStatus.Failed);
        batch.SkippedMailboxes = entries.Count(e => e.Status == MailboxMigrationStatus.Skipped);

        if (batch.Status is BatchStatus.Completed or BatchStatus.Failed)
        {
            var attempted = batch.TotalMailboxes - batch.SkippedMailboxes;
            batch.Status = (attempted > 0 && batch.FailedMailboxes == attempted)
                ? BatchStatus.Failed
                : BatchStatus.Completed;
            if (batch.Status == BatchStatus.Completed)
                batch.ErrorMessage = batch.SyncedMailboxes == 0 && batch.SkippedMailboxes > 0
                    ? $"All {batch.SkippedMailboxes} mailbox(es) were skipped — nothing was migrated."
                    : null;
        }

        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_ENTRY_SKIPPED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}/entries/{entryId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","entryId":"{{{entryId}}}","previousStatus":"{{{previousStatus.ToCamelCase()}}}","upn":"{{{entry.SourceUpn}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Mailbox entry {EntryId} ({Upn}) on batch {BatchId} reclassified from {Prev} → Skipped.",
            entryId, entry.SourceUpn, batchId, previousStatus);

        return Ok(MapEntryToResponse(entry));
    }

    /// <summary>
    /// Bulk-reclassify every currently-<c>Failed</c> entry on a batch as
    /// <c>Skipped</c>. Intended for terminal batches where the failures represent
    /// unmappable targets rather than real migration errors — the batch's status
    /// and progress are recomputed so it no longer reports as Failed.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/skip-failures")]
    public async Task<IActionResult> SkipFailures(
        Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        var entries = (await _batches.GetEntriesByBatchAsync(batchId, ct)).ToList();
        var failed = entries.Where(e => e.Status == MailboxMigrationStatus.Failed).ToList();
        if (failed.Count == 0)
            return Ok(MapToResponse(batch));

        foreach (var e in failed)
        {
            e.Status = MailboxMigrationStatus.Skipped;
            e.LastUpdated = DateTime.UtcNow;
        }

        batch.SyncedMailboxes  = entries.Count(e => e.Status == MailboxMigrationStatus.Synced);
        batch.FailedMailboxes  = entries.Count(e => e.Status == MailboxMigrationStatus.Failed);
        batch.SkippedMailboxes = entries.Count(e => e.Status == MailboxMigrationStatus.Skipped);

        if (batch.Status is BatchStatus.Completed or BatchStatus.Failed)
        {
            var attempted = batch.TotalMailboxes - batch.SkippedMailboxes;
            batch.Status = (attempted > 0 && batch.FailedMailboxes == attempted)
                ? BatchStatus.Failed
                : BatchStatus.Completed;
            // A batch that "completes" purely by skipping everything migrated
            // nothing — say so, or the Completed status reads as success (a live
            // run was mistaken for a successful migration this way).
            batch.ErrorMessage = batch.Status == BatchStatus.Completed &&
                                 batch.SyncedMailboxes == 0 && batch.SkippedMailboxes > 0
                ? $"All {batch.SkippedMailboxes} mailbox(es) were skipped — nothing was migrated."
                : null;
        }

        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_FAILURES_SKIPPED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","reclassifiedCount":{{{failed.Count}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Batch {BatchId}: reclassified {Count} failed entries as Skipped; status now {Status}.",
            batchId, failed.Count, batch.Status);

        return Ok(MapToResponse(batch));
    }

    /// <summary>Delete a mailbox migration batch and all its entries.</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{batchId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid batchId, CancellationToken ct)
    {
        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId) return NotFound();

        if (batch.Status is BatchStatus.Syncing or BatchStatus.Synced or BatchStatus.Completing)
            return UnprocessableEntity(new { message = "Cannot delete an active batch. Stop it first." });

        await _batches.DeleteBatchAsync(batchId, ct);
        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "MAILBOX_BATCH_DELETED",
            Resource  = $"projects/{projectId}/mailbox-batches/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","name":"{{{batch.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return NoContent();
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static MailboxBatchResponse MapToResponse(MailboxMigrationBatch b)
    {
        // Skipped entries aren't "attempted" — exclude them from both numerator
        // and denominator so a batch of 1 mapped + 1 skipped shows 100% on success.
        var effectiveTotal = Math.Max(0, b.TotalMailboxes - b.SkippedMailboxes);
        var processed = b.SyncedMailboxes + b.FailedMailboxes;
        var pct = effectiveTotal > 0
            ? Math.Round((double)processed / effectiveTotal * 100, 1)
            : (b.SkippedMailboxes > 0 ? 100.0 : 0.0);

        return new MailboxBatchResponse(
            Id:                   b.Id,
            ProjectId:            b.ProjectId,
            Name:                 b.Name,
            Status:               b.Status.ToCamelCase(),
            Strategy:             b.Strategy.ToCamelCase(),
            TotalMailboxes:       b.TotalMailboxes,
            SyncedMailboxes:      b.SyncedMailboxes,
            FailedMailboxes:      b.FailedMailboxes,
            SkippedMailboxes:     b.SkippedMailboxes,
            ProgressPercent:      pct,
            ExoMigrationBatchId:  b.ExoMigrationBatchId,
            TargetFolderName:     b.TargetFolderName,
            ErrorMessage:         b.ErrorMessage,
            CreatedAt:            b.CreatedAt,
            StartedAt:            b.StartedAt,
            CompletedAt:          b.CompletedAt,
            LastSyncedAt:         b.LastSyncedAt
        );
    }

    /// <summary>
    /// Parse the user-supplied strategy string. Accepts either the camelCase wire format
    /// (<c>graphCopy</c>/<c>nativeMrs</c>) or the C# enum names. Defaults to NativeMrs
    /// when null/blank — the Microsoft-native cross-tenant move is the recommended
    /// transport (server-side, live-validated end-to-end); Graph copy is the explicit
    /// fallback for tenant pairs without the cross-tenant EXO setup. Unrecognized
    /// values also fall back to GraphCopy (the no-setup transport) rather than failing.
    /// </summary>
    private static MailboxMigrationStrategy ParseStrategy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return MailboxMigrationStrategy.NativeMrs;

        return raw.Trim().ToLowerInvariant() switch
        {
            "nativemrs" or "native_mrs" or "native-mrs" or "mrs"
                => MailboxMigrationStrategy.NativeMrs,
            _ => MailboxMigrationStrategy.GraphCopy,
        };
    }

    private static MailboxEntryResponse MapEntryToResponse(MailboxMigrationEntry e) =>
        new(
            Id:                e.Id,
            BatchId:           e.BatchId,
            SourceUpn:         e.SourceUpn,
            TargetUpn:         e.TargetUpn,
            Status:            e.Status.ToCamelCase(),
            ItemsSyncedPercent: e.ItemsSyncedPercent,
            MessagesCopied:    e.MessagesCopied,
            TotalMessages:     e.TotalMessages,
            ErrorMessage:      e.ErrorMessage,
            LastUpdated:       e.LastUpdated
        );
}
