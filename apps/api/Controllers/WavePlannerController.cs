using Azure.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services.Spo;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Manages migration waves for a project.
///
/// A wave groups mailbox migration batches and/or content migration jobs into a
/// named, optionally time-scheduled phase.  Waves can be started immediately
/// (POST .../start) or scheduled to start automatically at a future UTC time
/// (set <c>scheduledStartAt</c> in the create/update request and transition via
/// POST .../schedule).  The <see cref="Workers.WaveSchedulerService"/> polls every
/// 60 seconds and auto-starts any due Scheduled waves.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/waves")]
[Authorize]
public class WavePlannerController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IWaveRepository _waves;
    private readonly IMailboxMigrationRepository _batches;
    private readonly IContentMigrationRepository _contentJobs;
    private readonly IUserMigrationRepository _userBatches;
    private readonly IAuditRepository _audit;
    private readonly MailboxMigrationQueue _mailboxQueue;
    private readonly ContentMigrationQueue _contentQueue;
    private readonly UserMigrationQueue _userMigrationQueue;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly ITenantCredentialFactory _credentialFactory;
    private readonly IExoRestClient _exoClient;
    private readonly ISpoRestClient _spoClient;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<WavePlannerController> _logger;

    public WavePlannerController(
        IProjectRepository projects,
        IWaveRepository waves,
        IMailboxMigrationRepository batches,
        IContentMigrationRepository contentJobs,
        IUserMigrationRepository userBatches,
        IAuditRepository audit,
        MailboxMigrationQueue mailboxQueue,
        ContentMigrationQueue contentQueue,
        UserMigrationQueue userMigrationQueue,
        IKeyVaultCredentialService keyVault,
        ITenantCredentialFactory credentialFactory,
        IExoRestClient exoClient,
        ISpoRestClient spoClient,
        ICurrentUserService currentUser,
        ILogger<WavePlannerController> logger)
    {
        _projects           = projects;
        _waves              = waves;
        _batches            = batches;
        _contentJobs        = contentJobs;
        _userBatches        = userBatches;
        _audit              = audit;
        _mailboxQueue       = mailboxQueue;
        _contentQueue       = contentQueue;
        _userMigrationQueue = userMigrationQueue;
        _keyVault           = keyVault;
        _credentialFactory  = credentialFactory;
        _exoClient          = exoClient;
        _spoClient          = spoClient;
        _currentUser = currentUser;
        _logger             = logger;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>List all waves for the project, ordered by wave number.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var waves = await _waves.GetWavesByProjectAsync(projectId, ct);

        // Load detail for each wave so we can populate batch/job summaries
        var result = new List<WaveResponse>();
        foreach (var wave in waves)
        {
            var detail = await _waves.GetWaveWithDetailsAsync(wave.Id, ct) ?? wave;
            result.Add(MapToResponse(detail));
        }

        return Ok(result);
    }

    /// <summary>Get a single wave with its assigned batches and jobs.</summary>
    [HttpGet("{waveId:guid}")]
    public async Task<IActionResult> GetById(Guid projectId, Guid waveId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveWithDetailsAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        return Ok(MapToResponse(wave));
    }

    /// <summary>Create a new wave in Draft status.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateWaveRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Wave name is required.");

        if (req.Order < 1)
            return BadRequest("Order must be >= 1.");

        var wave = new MigrationWave
        {
            ProjectId       = projectId,
            Name            = req.Name.Trim(),
            Description     = req.Description?.Trim(),
            Order           = req.Order,
            Status          = WaveStatus.Draft,
            ScheduledStartAt = req.ScheduledStartAt,
        };

        await _waves.AddWaveAsync(wave, ct);
        await _waves.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "WAVE_CREATED",
            Resource  = $"projects/{projectId}/waves/{wave.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"waveId":"{{{wave.Id}}}","name":"{{{wave.Name}}}","order":{{{wave.Order}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Wave {WaveId} ({Name}) created for project {ProjectId}.", wave.Id, wave.Name, projectId);

        return CreatedAtAction(nameof(GetById), new { projectId, waveId = wave.Id }, MapToResponse(wave));
    }

    /// <summary>Update wave name, description, order, or scheduled start time (Draft/Scheduled only).</summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{waveId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId,
        Guid waveId,
        [FromBody] UpdateWaveRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveByIdAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status == WaveStatus.Running ||
            wave.Status == WaveStatus.Completed ||
            wave.Status == WaveStatus.Failed ||
            wave.Status == WaveStatus.Cancelled)
            return BadRequest($"Cannot update a wave in {wave.Status} status.");

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Wave name is required.");

        if (req.Order < 1)
            return BadRequest("Order must be >= 1.");

        wave.Name            = req.Name.Trim();
        wave.Description     = req.Description?.Trim();
        wave.Order           = req.Order;
        wave.ScheduledStartAt = req.ScheduledStartAt;

        // If a schedule is provided and the wave is Draft, auto-transition to Scheduled
        if (req.ScheduledStartAt.HasValue && wave.Status == WaveStatus.Draft)
            wave.Status = WaveStatus.Scheduled;

        // If the schedule is cleared, revert to Draft
        if (!req.ScheduledStartAt.HasValue && wave.Status == WaveStatus.Scheduled)
            wave.Status = WaveStatus.Draft;

        await _waves.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "WAVE_UPDATED",
            Resource  = $"projects/{projectId}/waves/{waveId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"waveId":"{{{waveId}}}","name":"{{{wave.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        var detail = await _waves.GetWaveWithDetailsAsync(waveId, ct) ?? wave;
        return Ok(MapToResponse(detail));
    }

    /// <summary>Delete a Draft wave (cannot delete active/completed waves).</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{waveId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid waveId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveByIdAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status != WaveStatus.Draft && wave.Status != WaveStatus.Scheduled)
            return BadRequest($"Only Draft or Scheduled waves can be deleted. Current status: {wave.Status}.");

        // Unassign batches/jobs before deleting (the FK is SetNull, but let's be explicit)
        var waveBatches = await _batches.GetBatchesByProjectAsync(projectId, ct);
        foreach (var batch in waveBatches.Where(b => b.WaveId == waveId))
            batch.WaveId = null;
        await _batches.SaveAsync(ct);

        var waveJobs = await _contentJobs.GetJobsByProjectAsync(projectId, ct);
        foreach (var job in waveJobs.Where(j => j.WaveId == waveId))
            job.WaveId = null;
        await _contentJobs.SaveAsync(ct);

        var waveUserBatches = await _userBatches.GetBatchesByProjectAsync(projectId, ct);
        foreach (var ub in waveUserBatches.Where(b => b.WaveId == waveId))
            ub.WaveId = null;
        await _userBatches.SaveAsync(ct);

        // EF Core tracks the entity via FindAsync; remove it
        var dbContext = HttpContext.RequestServices
            .GetRequiredService<MigrationPlatform.Api.Data.AppDbContext>();
        dbContext.MigrationWaves.Remove(wave);
        await dbContext.SaveChangesAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "WAVE_DELETED",
            Resource  = $"projects/{projectId}/waves/{waveId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"waveId":"{{{waveId}}}","name":"{{{wave.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("Wave {WaveId} deleted for project {ProjectId}.", waveId, projectId);
        return NoContent();
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    /// <summary>Assign mailbox batches to this wave (replaces existing assignment).</summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{waveId:guid}/batches")]
    public async Task<IActionResult> AssignBatches(
        Guid projectId,
        Guid waveId,
        [FromBody] AssignBatchesToWaveRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveByIdAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status == WaveStatus.Running ||
            wave.Status == WaveStatus.Completed ||
            wave.Status == WaveStatus.Failed ||
            wave.Status == WaveStatus.Cancelled)
            return BadRequest($"Cannot modify batch assignments for a wave in {wave.Status} status.");

        // Clear existing assignments for this wave
        var existing = await _batches.GetBatchesByProjectAsync(projectId, ct);
        foreach (var batch in existing.Where(b => b.WaveId == waveId))
            batch.WaveId = null;

        // Apply new assignments, validating each batch belongs to this project
        foreach (var batchId in req.BatchIds)
        {
            var batch = await _batches.GetBatchByIdAsync(batchId, ct);
            if (batch is null || batch.ProjectId != projectId)
                return BadRequest($"Batch {batchId} not found in this project.");
            batch.WaveId = waveId;
        }

        await _batches.SaveAsync(ct);

        var detail = await _waves.GetWaveWithDetailsAsync(waveId, ct) ?? wave;
        return Ok(MapToResponse(detail));
    }

    /// <summary>Assign content jobs to this wave (replaces existing assignment).</summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{waveId:guid}/content-jobs")]
    public async Task<IActionResult> AssignContentJobs(
        Guid projectId,
        Guid waveId,
        [FromBody] AssignJobsToWaveRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveByIdAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status == WaveStatus.Running ||
            wave.Status == WaveStatus.Completed ||
            wave.Status == WaveStatus.Failed ||
            wave.Status == WaveStatus.Cancelled)
            return BadRequest($"Cannot modify job assignments for a wave in {wave.Status} status.");

        // Clear existing assignments for this wave
        var existing = await _contentJobs.GetJobsByProjectAsync(projectId, ct);
        foreach (var job in existing.Where(j => j.WaveId == waveId))
            job.WaveId = null;

        // Apply new assignments
        foreach (var jobId in req.JobIds)
        {
            var job = await _contentJobs.GetJobByIdAsync(jobId, ct);
            if (job is null || job.ProjectId != projectId)
                return BadRequest($"Content job {jobId} not found in this project.");
            job.WaveId = waveId;
        }

        await _contentJobs.SaveAsync(ct);

        var detail = await _waves.GetWaveWithDetailsAsync(waveId, ct) ?? wave;
        return Ok(MapToResponse(detail));
    }

    /// <summary>Assign user migration batches to this wave (replaces existing assignment).</summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{waveId:guid}/user-batches")]
    public async Task<IActionResult> AssignUserBatches(
        Guid projectId,
        Guid waveId,
        [FromBody] AssignUserBatchesToWaveRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveByIdAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status == WaveStatus.Running ||
            wave.Status == WaveStatus.Completed ||
            wave.Status == WaveStatus.Failed ||
            wave.Status == WaveStatus.Cancelled)
            return BadRequest($"Cannot modify user batch assignments for a wave in {wave.Status} status.");

        var existing = await _userBatches.GetBatchesByProjectAsync(projectId, ct);
        foreach (var batch in existing.Where(b => b.WaveId == waveId))
            batch.WaveId = null;

        foreach (var batchId in req.BatchIds)
        {
            var batch = await _userBatches.GetBatchByIdAsync(batchId, ct);
            if (batch is null || batch.ProjectId != projectId)
                return BadRequest($"User batch {batchId} not found in this project.");
            batch.WaveId = waveId;
        }

        await _userBatches.SaveAsync(ct);

        var detail = await _waves.GetWaveWithDetailsAsync(waveId, ct) ?? wave;
        return Ok(MapToResponse(detail));
    }

    // ── Lifecycle actions ─────────────────────────────────────────────────────

    /// <summary>
    /// Start a Draft or Scheduled wave immediately — transitions it to Running and
    /// enqueues all assigned Draft batches and jobs into their respective workers.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{waveId:guid}/start")]
    public async Task<IActionResult> Start(Guid projectId, Guid waveId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveWithDetailsAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status != WaveStatus.Draft && wave.Status != WaveStatus.Scheduled)
            return BadRequest($"Only Draft or Scheduled waves can be started. Current status: {wave.Status}.");

        // Load project with both tenants for credential building.
        // Cross-tenant migration batches are created in the TARGET tenant.
        var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
        if (project?.SourceTenant is null || project?.TargetTenant is null)
            return UnprocessableEntity(new { message = "Project, source tenant, or target tenant not found." });

        var sourceTenant = project.SourceTenant;
        var targetTenant = project.TargetTenant;

        var draftBatches    = wave.MailboxBatches.Where(b => b.Status == BatchStatus.Draft).ToList();
        var draftJobs       = wave.ContentJobs.Where(j =>
            j.Status == ContentMigrationJobStatus.Draft ||
            j.Status == ContentMigrationJobStatus.Scheduled).ToList();
        var draftUserBatches = wave.UserBatches.Where(b => b.Status == UserMigrationBatchStatus.Draft).ToList();

        // Build target tenant credential for EXO mailbox migration operations
        TokenCredential? targetCredential = null;
        if (draftBatches.Count > 0)
        {
            var (kvCertBase64, kvCertPassword, kvSecret) = await _keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
            try
            {
                targetCredential = _credentialFactory.CreateCredential(targetTenant, kvCertBase64, kvCertPassword, kvSecret);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { message = $"Target tenant credentials not available: {ex.Message}" });
            }
        }

        // User migration batches provision directly into the target tenant via
        // Graph POST /users — no source credential is required at wave-start time.

        // ── Validate EXO prerequisites if there are mailbox batches ───────────
        string? exoEndpoint = null;
        string? targetDeliveryDomain = null;

        if (draftBatches.Count > 0)
        {
            var targetPrefix = targetTenant.OnMicrosoftDomain;
            if (string.IsNullOrWhiteSpace(targetPrefix))
                return UnprocessableEntity(new
                {
                    message = "Target tenant OnMicrosoftDomain not detected. Re-verify the target tenant to auto-detect it."
                });

            targetDeliveryDomain = $"{targetPrefix}.mail.onmicrosoft.com";

            try
            {
                exoEndpoint = await _exoClient.FindCrossTenantMigrationEndpointAsync(
                    targetTenant.TenantId, targetCredential!, ct);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { message = ex.Message });
            }

            if (exoEndpoint is null)
                return UnprocessableEntity(new
                {
                    message = "No cross-tenant migration endpoint configured in the target tenant's EXO. " +
                              "Run 'Setup Exchange Migration' or create one manually via PowerShell."
                });
        }

        // ── Validate SPO prerequisites if there are content jobs ─────────────
        string? spoAdminUrl    = null;
        string? targetHostUrl  = null;
        SpoPowerShellCredentials? spoCredentials = null;
        if (draftJobs.Count > 0)
        {
            spoAdminUrl   = $"https://{sourceTenant.OnMicrosoftDomain}-admin.sharepoint.com";
            targetHostUrl = string.IsNullOrWhiteSpace(project.TargetTenant?.OnMicrosoftDomain)
                ? $"https://{sourceTenant.OnMicrosoftDomain}-my.sharepoint.com"
                : $"https://{project.TargetTenant.OnMicrosoftDomain}-my.sharepoint.com";

            var (srcCertB64, srcCertPwd) = await _keyVault.LoadCertificateWithFallbackAsync(sourceTenant, ct);
            if (string.IsNullOrEmpty(srcCertB64) ||
                string.IsNullOrWhiteSpace(sourceTenant.AppClientId) ||
                string.IsNullOrWhiteSpace(sourceTenant.TenantId))
            {
                return UnprocessableEntity(new
                {
                    message = "Source tenant is missing an app-only certificate (Key Vault or tenant record), " +
                              "AppClientId, or TenantId. SPO cross-tenant cmdlets require all three."
                });
            }
            spoCredentials = new SpoPowerShellCredentials(
                sourceTenant.TenantId, sourceTenant.AppClientId, srcCertB64, srcCertPwd);
        }

        // Everything validated — transition wave to Running
        wave.Status    = WaveStatus.Running;
        wave.StartedAt = DateTime.UtcNow;
        await _waves.SaveAsync(ct);

        // ── Start mailbox batches via EXO ─────────────────────────────────────
        foreach (var batch in draftBatches)
        {
            try
            {
                var entries   = await _batches.GetEntriesByBatchAsync(batch.Id, ct);
                var sourceUpns = entries.Select(e => e.SourceUpn).ToList();

                var result = await _exoClient.CreateMigrationBatchAsync(
                    targetTenant.TenantId,
                    batch.Name,
                    targetDeliveryDomain!,
                    exoEndpoint!,
                    sourceUpns,
                    targetCredential!,
                    ct);

                batch.ExoMigrationBatchId = result.BatchId;
                batch.Status    = BatchStatus.Syncing;
                batch.StartedAt = DateTime.UtcNow;
                _mailboxQueue.Channel.Writer.TryWrite(batch.Id);

                _logger.LogInformation(
                    "Wave {WaveId}: EXO batch created for mailbox batch {BatchId}. EXO ID: {ExoId}.",
                    waveId, batch.Id, result.BatchId);
            }
            catch (InvalidOperationException ex)
            {
                batch.Status       = BatchStatus.Failed;
                batch.ErrorMessage = $"Wave start failed to create EXO batch: {ex.Message}";
                batch.CompletedAt  = DateTime.UtcNow;
                _logger.LogWarning(ex,
                    "Wave {WaveId}: failed to create EXO batch for mailbox batch {BatchId}.",
                    waveId, batch.Id);
            }
        }
        await _batches.SaveAsync(ct);

        // ── Start content jobs via SPO PowerShell (Start-SPOCrossTenantUserContentMove) ──
        foreach (var job in draftJobs)
        {
            try
            {
                var items     = (await _contentJobs.GetItemsByJobAsync(job.Id, ct)).ToList();
                var spoJobIds = new List<string>();

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.OwnerUpn) || string.IsNullOrWhiteSpace(item.TargetOwnerUpn))
                    {
                        item.Status       = ContentMigrationItemStatus.Failed;
                        item.ErrorMessage = "OneDrive item requires both OwnerUpn and TargetOwnerUpn.";
                        item.LastUpdated  = DateTime.UtcNow;
                        continue;
                    }

                    var result = await _spoClient.StartUserContentMoveAsync(
                        spoAdminUrl!, item.OwnerUpn!, item.TargetOwnerUpn!, targetHostUrl!, spoCredentials!, ct);
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
                    "Wave {WaveId}: SPO jobs created for content job {JobId}. Count: {Count}.",
                    waveId, job.Id, spoJobIds.Count);
            }
            catch (InvalidOperationException ex)
            {
                job.Status       = ContentMigrationJobStatus.Failed;
                job.ErrorMessage = $"Wave start failed to create SPO job: {ex.Message}";
                job.CompletedAt  = DateTime.UtcNow;
                _logger.LogWarning(ex,
                    "Wave {WaveId}: failed to create SPO job for content job {JobId}.",
                    waveId, job.Id);
            }
        }
        await _contentJobs.SaveAsync(ct);

        // ── Start Draft user migration batches via Graph POST /users ──────────
        foreach (var ub in draftUserBatches)
        {
            ub.Status    = UserMigrationBatchStatus.Provisioning;
            ub.StartedAt = DateTime.UtcNow;
            _userMigrationQueue.Channel.Writer.TryWrite(ub.Id);

            _logger.LogInformation(
                "Wave {WaveId}: user migration batch {BatchId} enqueued ({Count} users).",
                waveId, ub.Id, ub.TotalUsers);
        }
        await _userBatches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "WAVE_STARTED",
            Resource  = $"projects/{projectId}/waves/{waveId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"waveId":"{{{waveId}}}","batchCount":{{{draftBatches.Count}}},"jobCount":{{{draftJobs.Count}}},"userBatchCount":{{{draftUserBatches.Count}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Wave {WaveId} started for project {ProjectId}. Batches={Batches}, Jobs={Jobs}, UserBatches={UserBatches}.",
            waveId, projectId, draftBatches.Count, draftJobs.Count, draftUserBatches.Count);

        var detail = await _waves.GetWaveWithDetailsAsync(waveId, ct) ?? wave;
        return Ok(MapToResponse(detail));
    }

    /// <summary>Cancel a Draft, Scheduled, or Running wave.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{waveId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid projectId, Guid waveId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var wave = await _waves.GetWaveWithDetailsAsync(waveId, ct);
        if (wave is null || wave.ProjectId != projectId)
            return NotFound($"Wave {waveId} not found.");

        if (wave.Status == WaveStatus.Completed ||
            wave.Status == WaveStatus.Failed ||
            wave.Status == WaveStatus.Cancelled)
            return BadRequest($"Wave is already in terminal status {wave.Status}.");

        wave.Status      = WaveStatus.Cancelled;
        wave.CompletedAt = DateTime.UtcNow;

        // Stop any Syncing batches belonging to this wave
        foreach (var batch in wave.MailboxBatches.Where(b => b.Status == BatchStatus.Syncing))
            batch.Status = BatchStatus.Stopped;
        await _batches.SaveAsync(ct);

        // Cancel any Running/Paused content jobs belonging to this wave
        foreach (var job in wave.ContentJobs.Where(
            j => j.Status == ContentMigrationJobStatus.Running ||
                 j.Status == ContentMigrationJobStatus.Paused ||
                 j.Status == ContentMigrationJobStatus.Scheduled))
        {
            job.Status = ContentMigrationJobStatus.Failed;
            job.ErrorMessage = "Cancelled by wave cancellation.";
        }
        await _contentJobs.SaveAsync(ct);

        // Stop any provisioning user migration batches belonging to this wave
        foreach (var ub in wave.UserBatches.Where(
            b => b.Status == UserMigrationBatchStatus.Provisioning))
        {
            ub.Status      = UserMigrationBatchStatus.Stopped;
            ub.CompletedAt = DateTime.UtcNow;
        }
        await _userBatches.SaveAsync(ct);

        await _waves.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "WAVE_CANCELLED",
            Resource  = $"projects/{projectId}/waves/{waveId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"waveId":"{{{waveId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("Wave {WaveId} cancelled for project {ProjectId}.", waveId, projectId);
        return Ok(MapToResponse(wave));
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static WaveResponse MapToResponse(MigrationWave w)
    {
        var batches = w.MailboxBatches.Select(b =>
        {
            var pct = b.TotalMailboxes > 0
                ? Math.Round((double)b.SyncedMailboxes / b.TotalMailboxes * 100, 1)
                : 0.0;
            return new WaveBatchSummary(b.Id, b.Name, b.Status.ToCamelCase(),
                b.TotalMailboxes, b.SyncedMailboxes, b.FailedMailboxes, pct);
        }).ToList();

        var jobs = w.ContentJobs.Select(j =>
        {
            var pct = j.TotalItems > 0
                ? Math.Round((double)j.MigratedItems / j.TotalItems * 100, 1)
                : 0.0;
            return new WaveJobSummary(j.Id, j.Name, j.JobType.ToCamelCase(), j.Status.ToCamelCase(),
                j.TotalItems, j.MigratedItems, j.FailedItems, pct);
        }).ToList();

        var userBatches = w.UserBatches.Select(b =>
        {
            var pct = b.TotalUsers > 0
                ? Math.Round((double)b.ProvisionedUsers / b.TotalUsers * 100, 1)
                : 0.0;
            return new WaveUserBatchSummary(b.Id, b.Name, b.Status.ToCamelCase(),
                b.TotalUsers, b.ProvisionedUsers, b.FailedUsers, pct);
        }).ToList();

        return new WaveResponse(
            Id:              w.Id,
            ProjectId:       w.ProjectId,
            Name:            w.Name,
            Description:     w.Description,
            Order:           w.Order,
            Status:          w.Status.ToCamelCase(),
            ScheduledStartAt: w.ScheduledStartAt,
            CreatedAt:       w.CreatedAt,
            StartedAt:       w.StartedAt,
            CompletedAt:     w.CompletedAt,
            MailboxBatches:  batches,
            ContentJobs:     jobs,
            UserBatches:     userBatches);
    }
}
