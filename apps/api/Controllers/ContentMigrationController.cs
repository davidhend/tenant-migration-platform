using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services.Spo;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Orchestrates SharePoint and OneDrive cross-tenant content migration jobs for a project.
///
/// The Start action requires calling the SharePoint Migration API at
/// <c>https://{tenant}-admin.sharepoint.com/_api/site/CreateMigrationJob</c> with the
/// <c>Sites.FullControl.All</c> application permission — this returns 501 until wired.
/// See: https://learn.microsoft.com/en-us/sharepoint/dev/apis/migration-api-overview
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/content-migrations")]
[Authorize]
public class ContentMigrationController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IContentMigrationRepository _jobs;
    private readonly IIdentityMapRepository _identityMaps;
    private readonly IAuditRepository _audit;
    private readonly ContentMigrationQueue _queue;
    private readonly ITenantCredentialFactory _credentialFactory;
    private readonly ISpoRestClient _spoClient;
    private readonly IGraphClientFactory _graphFactory;
    private readonly IOneDriveProvisioningService _oneDriveProvisioning;
    private readonly ILicenseCheckService _licenseCheck;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ContentMigrationController> _logger;

    public ContentMigrationController(
        IProjectRepository projects,
        IContentMigrationRepository jobs,
        IIdentityMapRepository identityMaps,
        IAuditRepository audit,
        ContentMigrationQueue queue,
        ITenantCredentialFactory credentialFactory,
        ISpoRestClient spoClient,
        IGraphClientFactory graphFactory,
        IOneDriveProvisioningService oneDriveProvisioning,
        ILicenseCheckService licenseCheck,
        IKeyVaultCredentialService keyVault,
        IConfiguration configuration,
        ICurrentUserService currentUser,
        ILogger<ContentMigrationController> logger)
    {
        _projects = projects;
        _jobs = jobs;
        _identityMaps = identityMaps;
        _audit = audit;
        _queue = queue;
        _credentialFactory = credentialFactory;
        _spoClient = spoClient;
        _graphFactory = graphFactory;
        _oneDriveProvisioning = oneDriveProvisioning;
        _licenseCheck = licenseCheck;
        _keyVault = keyVault;
        _configuration = configuration;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// List all content migration jobs for the given project.
    /// Optionally filter by <paramref name="jobType"/> (<c>oneDrive</c> or <c>sharePoint</c>).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        Guid projectId,
        [FromQuery] string? jobType,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var results = await _jobs.GetJobsByProjectAsync(projectId, ct);

        if (!string.IsNullOrWhiteSpace(jobType))
        {
            if (!Enum.TryParse<ContentMigrationJobType>(jobType, ignoreCase: true, out var parsedType))
                return BadRequest($"Invalid jobType '{jobType}'. Valid values: oneDrive, sharePoint.");

            results = results.Where(j => j.JobType == parsedType);
        }

        return Ok(results.Select(MapToResponse));
    }

    /// <summary>
    /// Create a new content migration job in Draft status.
    /// The job is not submitted to the SharePoint Migration API until
    /// <c>POST .../start</c> is called.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateContentJobRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Job name is required.");

        if (!Enum.TryParse<ContentMigrationJobType>(req.JobType, ignoreCase: true, out var jobType))
            return BadRequest($"Invalid jobType '{req.JobType}'. Valid values: oneDrive, sharePoint.");

        if (req.Items is null || req.Items.Count == 0)
            return BadRequest("At least one item is required.");

        // Validate items based on job type:
        // - SharePoint: SourceUrl and TargetUrl are required (site URLs).
        // - OneDrive:   OwnerUpn and TargetOwnerUpn are required (UPN-based move);
        //               SourceUrl/TargetUrl are optional metadata (OneDrive root URLs).
        if (jobType == ContentMigrationJobType.SharePoint)
        {
            var invalid = req.Items
                .Where(i => string.IsNullOrWhiteSpace(i.SourceUrl) || string.IsNullOrWhiteSpace(i.TargetUrl))
                .ToList();
            if (invalid.Count > 0)
                return BadRequest("SharePoint items must have both SourceUrl and TargetUrl.");
        }
        else // OneDrive
        {
            var invalid = req.Items
                .Where(i => string.IsNullOrWhiteSpace(i.OwnerUpn) || string.IsNullOrWhiteSpace(i.TargetOwnerUpn))
                .ToList();
            if (invalid.Count > 0)
                return BadRequest("OneDrive items must have both OwnerUpn and TargetOwnerUpn.");
        }

        var job = new ContentMigrationJob
        {
            ProjectId  = projectId,
            Name       = req.Name.Trim(),
            JobType    = jobType,
            Status     = ContentMigrationJobStatus.Draft,
            TotalItems = req.Items.Count,
        };

        var items = req.Items.Select(i => new ContentMigrationItem
        {
            JobId     = job.Id,
            SourceUrl      = i.SourceUrl?.Trim() ?? string.Empty,
            TargetUrl      = i.TargetUrl?.Trim() ?? string.Empty,
            OwnerUpn       = string.IsNullOrWhiteSpace(i.OwnerUpn) ? null : i.OwnerUpn.Trim(),
            TargetOwnerUpn = string.IsNullOrWhiteSpace(i.TargetOwnerUpn) ? null : i.TargetOwnerUpn.Trim(),
            Status         = ContentMigrationItemStatus.Queued,
        }).ToList();

        await _jobs.AddJobAsync(job, ct);
        await _jobs.AddItemsAsync(items, ct);
        await _jobs.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "CONTENT_MIGRATION_JOB_CREATED",
            Resource  = $"projects/{projectId}/content-migrations/{job.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{job.Id}}}","name":"{{{job.Name}}}","jobType":"{{{job.JobType.ToCamelCase()}}}","itemCount":{{{job.TotalItems}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Content migration job {JobId} ({Name}, {JobType}) created for project {ProjectId} with {Count} items.",
            job.Id, job.Name, job.JobType, projectId, job.TotalItems);

        return CreatedAtAction(
            nameof(GetById),
            new { projectId, jobId = job.Id },
            MapToResponse(job));
    }

    /// <summary>Get a single content migration job by ID.</summary>
    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetById(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        return Ok(MapToResponse(job));
    }

    /// <summary>List all items within a content migration job.</summary>
    [HttpGet("{jobId:guid}/items")]
    public async Task<IActionResult> GetItems(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        var items = await _jobs.GetItemsByJobAsync(jobId, ct);
        return Ok(items.Select(MapItemToResponse));
    }

    /// <summary>
    /// Update a Draft job's name and/or replace its items.
    /// Only Draft jobs can be edited.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{jobId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId,
        Guid jobId,
        [FromBody] UpdateContentJobRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        if (job.Status != ContentMigrationJobStatus.Draft)
            return BadRequest("Only Draft jobs can be edited.");

        if (!string.IsNullOrWhiteSpace(req.Name))
            job.Name = req.Name.Trim();

        if (req.Items is not null)
        {
            if (req.Items.Count == 0)
                return BadRequest("At least one item is required.");

            IReadOnlyList<ContentItemRequest> invalid = job.JobType == ContentMigrationJobType.SharePoint
                ? req.Items.Where(i => string.IsNullOrWhiteSpace(i.SourceUrl) || string.IsNullOrWhiteSpace(i.TargetUrl)).ToList()
                : req.Items.Where(i => string.IsNullOrWhiteSpace(i.OwnerUpn) || string.IsNullOrWhiteSpace(i.TargetOwnerUpn)).ToList();
            if (invalid.Count > 0)
                return BadRequest(job.JobType == ContentMigrationJobType.SharePoint
                    ? "SharePoint items must have both SourceUrl and TargetUrl."
                    : "OneDrive items must have both OwnerUpn and TargetOwnerUpn.");

            await _jobs.RemoveItemsByJobAsync(jobId, ct);

            var newItems = req.Items.Select(i => new ContentMigrationItem
            {
                JobId          = jobId,
                SourceUrl      = i.SourceUrl?.Trim() ?? string.Empty,
                TargetUrl      = i.TargetUrl?.Trim() ?? string.Empty,
                OwnerUpn       = string.IsNullOrWhiteSpace(i.OwnerUpn) ? null : i.OwnerUpn.Trim(),
                TargetOwnerUpn = string.IsNullOrWhiteSpace(i.TargetOwnerUpn) ? null : i.TargetOwnerUpn.Trim(),
                Status         = ContentMigrationItemStatus.Queued,
            }).ToList();

            await _jobs.AddItemsAsync(newItems, ct);
            job.TotalItems = newItems.Count;
        }

        job.LastUpdatedAt = DateTime.UtcNow;
        await _jobs.SaveAsync(ct);

        return Ok(MapToResponse(job));
    }

    /// <summary>
    /// Start a Draft job — transitions it to Running and submits to the SharePoint
    /// Migration API (in production mode).
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/start")]
    public async Task<IActionResult> Start(
        Guid projectId,
        Guid jobId,
        [FromServices] OneDriveProvisioningQueue provisioningQueue,
        [FromServices] IProgressNotifier notifier,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        if (job.Status != ContentMigrationJobStatus.Draft &&
            job.Status != ContentMigrationJobStatus.Ready)
            return BadRequest($"Only Draft or Ready jobs can be started. Current status: {job.Status}.");

        // ── Mock mode short-circuit ───────────────────────────────────────────
        // When Platform:MockGraphCalls=true, skip all real credential and SPO API
        // calls and return a synthetic "started" response so the UI can be
        // exercised without real tenant credentials or a live SPO environment.
        if (_configuration.GetValue<bool>("Platform:MockGraphCalls"))
        {
            _logger.LogInformation(
                "MockGraphCalls=true — skipping real SPO job submission for content migration job {JobId}.",
                jobId);

            // Assign a synthetic SPO job ID for each item so the worker can
            // recognise that the job has been submitted.
            var mockItems = (await _jobs.GetItemsByJobAsync(jobId, ct)).ToList();
            var mockSpoIds = new List<string>();
            foreach (var item in mockItems)
            {
                var mockId = $"mock-spo-{Guid.NewGuid():N}";
                mockSpoIds.Add(mockId);
                item.SpoJobId = mockId;
                item.Status = ContentMigrationItemStatus.Running;
                item.LastUpdated = DateTime.UtcNow;
            }

            job.SpoMigrationJobId = string.Join(',', mockSpoIds);
            job.Status      = ContentMigrationJobStatus.Running;
            job.StartedAt   = DateTime.UtcNow;
            job.LastUpdatedAt = DateTime.UtcNow;

            await _jobs.SaveAsync(ct);
            _queue.Channel.Writer.TryWrite(jobId);

            await _audit.AddAsync(new AuditEvent
            {
                Action    = "CONTENT_MIGRATION_JOB_STARTED",
                Resource  = $"projects/{projectId}/content-migrations/{jobId}",
                Actor     = _currentUser.UserName,
                ProjectId = projectId,
                Details   = $$$"""{"jobId":"{{{jobId}}}","mock":true,"spoJobCount":{{{mockItems.Count}}}}""",
            }, ct);
            await _audit.SaveAsync(ct);

            return Ok(MapToResponse(job));
        }

        // Load project with both tenants (target admin URL is required by the cross-tenant API)
        var project = await _projects.GetByIdWithTenantsAsync(job.ProjectId, ct);
        if (project is null || project.SourceTenant is null)
            return UnprocessableEntity(new { message = "Project or source tenant not found." });

        var sourceTenant = project.SourceTenant;
        var targetTenant = project.TargetTenant;

        // Derive SPO admin URLs from each tenant's onmicrosoft.com prefix
        if (string.IsNullOrWhiteSpace(sourceTenant.OnMicrosoftDomain))
        {
            return UnprocessableEntity(new
            {
                message = "Source tenant OnMicrosoftDomain not detected. " +
                          "Re-verify the source tenant to auto-detect it."
            });
        }
        if (targetTenant is null || string.IsNullOrWhiteSpace(targetTenant.OnMicrosoftDomain))
        {
            return UnprocessableEntity(new
            {
                message = "Target tenant OnMicrosoftDomain not detected. " +
                          "Re-verify the target tenant to auto-detect it."
            });
        }

        var sourceAdminUrl = $"https://{sourceTenant.OnMicrosoftDomain}-admin.sharepoint.com";
        var targetAdminUrl = $"https://{targetTenant.OnMicrosoftDomain}-admin.sharepoint.com";

        // The cross-tenant host URL is what Get-SPOCrossTenantHostUrl returns on the
        // TARGET tenant and what the MnA relationship was established with — the
        // "-my.sharepoint.com" host for standard tenants, for BOTH user and site
        // moves. The worker derives the same value when polling, so this must stay
        // deterministic. A best-effort live resolution below warns on any mismatch.
        var targetHostUrl = $"https://{targetTenant.OnMicrosoftDomain}-my.sharepoint.com";

        // Load source tenant app-only cert for pwsh Connect-SPOService
        SpoPowerShellCredentials sourceSpoCreds;
        try
        {
            sourceSpoCreds = await BuildSpoCredentialsAsync(sourceTenant, ct);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = $"Source tenant credentials not available: {ex.Message}" });
        }

        // Target tenant credentials are required for the identity-map upload (the
        // map must be uploaded by the TARGET tenant per Microsoft's flow).
        SpoPowerShellCredentials targetSpoCreds;
        try
        {
            targetSpoCreds = await BuildSpoCredentialsAsync(targetTenant, ct);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new
            {
                message = $"Target tenant credentials not available: {ex.Message} " +
                          "The cross-tenant identity map must be uploaded to the target tenant before content moves can start."
            });
        }

        // Pre-flight: verify the cross-tenant relationship is compatible. When it
        // isn't established yet, attempt to establish it automatically (both sides
        // via the runbook) before failing the start.
        var compatResult = await _spoClient.CheckCrossTenantCompatibilityAsync(
            sourceAdminUrl, targetHostUrl, sourceSpoCreds, ct);
        string? autoEstablishDetail = null;
        if (!compatResult.IsCompatible &&
            compatResult.ErrorMessage is null &&
            _configuration.GetValue("ContentMigration:AutoEstablishRelationship", true))
        {
            _logger.LogInformation(
                "Cross-tenant relationship for job {JobId} not ready (status '{Status}') — attempting automatic establishment.",
                jobId, compatResult.Status);

            var sourceHostUrl = $"https://{sourceTenant.OnMicrosoftDomain}-my.sharepoint.com";
            var rel = await _spoClient.EnsureCrossTenantRelationshipAsync(
                sourceAdminUrl, targetAdminUrl, sourceHostUrl, targetHostUrl,
                sourceSpoCreds, targetSpoCreds, skipPrecheck: true, ct);

            if (rel.IsEstablished)
            {
                compatResult = new SpoCompatibilityResult(true, rel.CompatibilityStatus, null);
                _logger.LogInformation(
                    "Cross-tenant relationship established automatically (compatibility '{Status}'; target-side test '{Target}', source-side test '{Source}').",
                    rel.CompatibilityStatus, rel.TargetSideTestStatus, rel.SourceSideTestStatus);

                await _audit.AddAsync(new AuditEvent
                {
                    Action    = "CONTENT_CROSS_TENANT_RELATIONSHIP_ESTABLISHED",
                    Resource  = $"projects/{projectId}/content-migrations/{jobId}",
                    Actor     = _currentUser.UserName,
                    ProjectId = projectId,
                    Details   = $$$"""{"jobId":"{{{jobId}}}","compatibilityStatus":"{{{rel.CompatibilityStatus}}}"}""",
                }, ct);
                await _audit.SaveAsync(ct);
            }
            else
            {
                autoEstablishDetail =
                    $" Automatic establishment was attempted and did not succeed: {rel.ErrorMessage ?? "unknown error"}" +
                    $" (target-side test: '{rel.TargetSideTestStatus ?? "not run"}', source-side test: '{rel.SourceSideTestStatus ?? "not run"}').";
            }
        }

        if (!compatResult.IsCompatible)
        {
            var detail = compatResult.ErrorMessage is not null
                ? $"The compatibility check itself failed: {compatResult.ErrorMessage}"
                : $"Get-SPOCrossTenantCompatibilityStatus returned '{compatResult.Status}'.{autoEstablishDetail} " +
                  "Run Set-SPOCrossTenantRelationship on both tenants and then verify with " +
                  $"Get-SPOCrossTenantCompatibilityStatus -PartnerCrossTenantHostURL {targetHostUrl}. " +
                  "Status must be 'Compatible' or 'Warning' before starting a migration.";

            return UnprocessableEntity(new { message = detail });
        }

        var items = (await _jobs.GetItemsByJobAsync(jobId, ct)).ToList();
        var isOneDrive = job.JobType == ContentMigrationJobType.OneDrive;

        // OneDrive pre-flight: check and trigger provisioning for target users
        if (isOneDrive)
        {
            var targetUpns = items
                .Where(i => !string.IsNullOrWhiteSpace(i.TargetOwnerUpn))
                .Select(i => i.TargetOwnerUpn!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targetUpns.Count > 0)
            {
                try
                {
                    var (tgtCert, tgtPw, tgtSecret) = await _keyVault.LoadCredentialsAsync(targetTenant!.Id, ct);
                    var targetGraph = _graphFactory.CreateForTenant(targetTenant, tgtCert, tgtPw, tgtSecret);

                    // The Cross Tenant User Data Migration license covers OneDrive
                    // moves too — ensure batch owners hold it (target side, same SKU
                    // the mailbox path assigns; never blocks the start).
                    if (_configuration.GetValue("ContentMigration:AutoAssignLicense", true))
                    {
                        try
                        {
                            var lic = await _licenseCheck.EnsureCrossTenantMigrationLicensesAsync(
                                targetGraph, targetUpns,
                                _configuration.GetValue("MailboxMigration:DefaultUsageLocation", "US") ?? "US",
                                ct);
                            if (lic.Assigned.Count > 0 || lic.Failed.Count > 0)
                                _logger.LogInformation(
                                    "Content job {JobId}: cross-tenant migration licenses — {Assigned} assigned, {Already} already licensed, {Failed} failed.",
                                    jobId, lic.Assigned.Count, lic.AlreadyLicensed.Count, lic.Failed.Count);
                            foreach (var f in lic.Failed)
                                _logger.LogWarning(
                                    "Content job {JobId}: license assignment failed for {Upn}: {Reason}",
                                    jobId, f.Upn, f.Reason);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Content job {JobId}: license auto-assignment errored — continuing (per-user stalls will surface in move state).",
                                jobId);
                        }
                    }

                    var provResults = await _oneDriveProvisioning.CheckAndProvisionBatchAsync(targetGraph, targetUpns, ct);

                    // Start-SPOCrossTenantUserContentMove CREATES the target personal
                    // site itself and fails with "The target tenant has a conflict for
                    // the site provided" when one already exists (confirmed live
                    // 2026-07-09). The gate is therefore INVERTED from the original
                    // design: an EXISTING target OneDrive blocks the move and must be
                    // removed first; missing drives are exactly what the move expects,
                    // so no pre-provisioning (Request-SPOPersonalSite) is needed — or
                    // wanted — before starting.
                    var alreadyProvisioned = provResults.Where(r => r.IsProvisioned).ToList();
                    if (alreadyProvisioned.Count > 0)
                    {
                        var conflicts = alreadyProvisioned.Select(r =>
                            $"{r.Upn} ({targetHostUrl}/personal/{r.Upn.Replace('.', '_').Replace('@', '_')})");
                        _logger.LogWarning(
                            "Content migration job {JobId}: {Count} target user(s) already have a OneDrive — blocking start (the cross-tenant move must create the target site). Users: {Users}",
                            jobId,
                            alreadyProvisioned.Count,
                            string.Join(", ", alreadyProvisioned.Select(r => r.Upn)));

                        return UnprocessableEntity(new
                        {
                            message =
                                "Target OneDrive(s) already exist — the cross-tenant move creates the target " +
                                "personal site itself and fails when one is present. Remove them on the target " +
                                "tenant (Remove-SPOSite, then Remove-SPODeletedSite to purge the recycle bin — " +
                                "a soft-deleted site still conflicts) and retry: " +
                                string.Join("; ", conflicts),
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OneDrive provisioning check failed — proceeding with migration.");
                }
            }
        }

        // Best-effort: resolve the target tenant's actual cross-tenant host URL and
        // warn when it differs from the derived "-my" host. The derived value is
        // still used for submission so start/poll stay consistent; a mismatch here
        // is the diagnostic for the rare vanity-host tenant.
        try
        {
            var resolvedHostUrl = await _spoClient.GetCrossTenantHostUrlAsync(targetAdminUrl, targetSpoCreds, ct);
            if (!string.IsNullOrWhiteSpace(resolvedHostUrl) &&
                !string.Equals(resolvedHostUrl!.TrimEnd('/'), targetHostUrl, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Target tenant Get-SPOCrossTenantHostUrl returned {Resolved} but the platform derived {Derived}. " +
                    "Cross-tenant moves for job {JobId} may fail — the MnA relationship must use the resolved URL.",
                    resolvedHostUrl, targetHostUrl, jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cross-tenant host URL resolution failed for job {JobId} — using derived host.", jobId);
        }

        // Upload the cross-tenant identity map to the TARGET tenant. SPO requires it
        // before content moves so source identities resolve to target identities;
        // each upload overwrites the previous map, so include the project's full
        // mapping set plus this job's item pairs.
        var identityMapError = await UploadIdentityMapForProjectAsync(
            projectId, job, sourceTenant, targetAdminUrl, targetSpoCreds, ct);
        if (identityMapError is not null)
            return UnprocessableEntity(new { message = identityMapError });

        var spoJobIds = new List<string>();

        // Reset item statuses for retried jobs
        foreach (var item in items)
        {
            item.Status = ContentMigrationItemStatus.Queued;
            item.ErrorMessage = null;
            item.SpoJobId = null;
            item.ProgressPercent = 0;
            item.LastUpdated = DateTime.UtcNow;
        }

        foreach (var item in items)
        {
            if (isOneDrive)
            {
                if (string.IsNullOrWhiteSpace(item.OwnerUpn) || string.IsNullOrWhiteSpace(item.TargetOwnerUpn))
                {
                    _logger.LogWarning("Item {ItemId} missing OwnerUpn or TargetOwnerUpn — skipping.", item.Id);
                    item.Status = ContentMigrationItemStatus.Failed;
                    item.ErrorMessage = "OneDrive item requires both OwnerUpn (source) and TargetOwnerUpn (target).";
                    item.LastUpdated = DateTime.UtcNow;
                    continue;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(item.SourceUrl) || string.IsNullOrWhiteSpace(item.TargetUrl))
                {
                    _logger.LogWarning("Item {ItemId} missing SourceUrl or TargetUrl — skipping.", item.Id);
                    item.Status = ContentMigrationItemStatus.Failed;
                    item.ErrorMessage = "SharePoint item requires both SourceUrl and TargetUrl.";
                    item.LastUpdated = DateTime.UtcNow;
                    continue;
                }
            }

            try
            {
                SpoMigrationJobResult result;
                if (isOneDrive)
                {
                    result = await _spoClient.StartUserContentMoveAsync(
                        sourceAdminUrl, item.OwnerUpn!, item.TargetOwnerUpn!, targetHostUrl, sourceSpoCreds, ct);
                }
                else
                {
                    result = await _spoClient.StartSiteContentMoveAsync(
                        sourceAdminUrl, item.SourceUrl!, item.TargetUrl!, targetHostUrl, sourceSpoCreds, ct);
                }

                spoJobIds.Add(result.JobId);
                item.SpoJobId    = result.JobId;
                item.Status      = ContentMigrationItemStatus.Running;
                item.LastUpdated = DateTime.UtcNow;
                _logger.LogInformation(
                    "Cross-tenant {Type} content move started for item {ItemId}.",
                    job.JobType, item.Id);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "SPO cross-tenant content move failed for item {ItemId}.",
                    item.Id);
                item.Status       = ContentMigrationItemStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.LastUpdated  = DateTime.UtcNow;
            }
        }

        if (spoJobIds.Count == 0)
        {
            job.FailedItems = items.Count(i => i.Status == ContentMigrationItemStatus.Failed);
            job.LastUpdatedAt = DateTime.UtcNow;
            await _jobs.SaveAsync(ct);
            var itemErrors = items
                .Where(i => !string.IsNullOrWhiteSpace(i.ErrorMessage))
                .Select(i => new { itemId = i.Id, error = i.ErrorMessage })
                .ToList();
            return UnprocessableEntity(new
            {
                message = "No items were successfully queued for migration.",
                itemErrors,
            });
        }

        // Re-read job to check if status changed during the long-running SPO calls
        // (e.g. user cancelled the job while Start was in-flight).
        var freshJob = await _jobs.GetJobByIdAsync(jobId, ct);
        if (freshJob is null ||
            (freshJob.Status != ContentMigrationJobStatus.Draft &&
             freshJob.Status != ContentMigrationJobStatus.Ready))
        {
            _logger.LogWarning(
                "Content migration job {JobId} status changed to {Status} during SPO submission — aborting.",
                jobId, freshJob?.Status);
            await _jobs.SaveAsync(ct);
            return Conflict(new { message = $"Job status changed to {freshJob?.Status.ToCamelCase() ?? "unknown"} during submission. SPO jobs may have been started — check item statuses." });
        }

        job.SpoMigrationJobId = string.Join(',', spoJobIds);
        job.Status = ContentMigrationJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.LastUpdatedAt = DateTime.UtcNow;

        await _jobs.SaveAsync(ct);

        // Enqueue so the worker begins polling SPO for status
        _queue.Channel.Writer.TryWrite(jobId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "CONTENT_MIGRATION_JOB_STARTED",
            Resource  = $"projects/{projectId}/content-migrations/{jobId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{jobId}}}","spoJobCount":{{{spoJobIds.Count}}},"adminUrl":"{{{sourceAdminUrl}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Content migration job {JobId} started. {Count} SPO migration jobs submitted to {AdminUrl}.",
            jobId, spoJobIds.Count, sourceAdminUrl);

        return Ok(MapToResponse(job));
    }

    /// <summary>
    /// Pause a Running job — transitions it to Paused.
    /// The background worker will stop advancing this job on its next tick.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/pause")]
    public async Task<IActionResult> Pause(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        if (job.Status != ContentMigrationJobStatus.Running)
            return BadRequest($"Only Running jobs can be paused. Current status: {job.Status}.");

        job.Status        = ContentMigrationJobStatus.Paused;
        job.LastUpdatedAt = DateTime.UtcNow;

        await _jobs.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "CONTENT_MIGRATION_JOB_PAUSED",
            Resource  = $"projects/{projectId}/content-migrations/{jobId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{jobId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Content migration job {JobId} paused for project {ProjectId}.", jobId, projectId);
        return Ok(MapToResponse(job));
    }

    /// <summary>
    /// Resume a Paused job — transitions it back to Running and re-enqueues it
    /// for the background worker to continue driving progress.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/resume")]
    public async Task<IActionResult> Resume(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        if (job.Status != ContentMigrationJobStatus.Paused)
            return BadRequest($"Only Paused jobs can be resumed. Current status: {job.Status}.");

        job.Status        = ContentMigrationJobStatus.Running;
        job.LastUpdatedAt = DateTime.UtcNow;

        await _jobs.SaveAsync(ct);

        // Re-enqueue so the worker picks it up immediately
        _queue.Channel.Writer.TryWrite(jobId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "CONTENT_MIGRATION_JOB_RESUMED",
            Resource  = $"projects/{projectId}/content-migrations/{jobId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{jobId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Content migration job {JobId} resumed for project {ProjectId}.", jobId, projectId);
        return Ok(MapToResponse(job));
    }

    /// <summary>
    /// Cancel a job in any active state (Draft, Scheduled, Running, Paused) —
    /// transitions it to Failed.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        var cancellableStatuses = new[]
        {
            ContentMigrationJobStatus.Draft,
            ContentMigrationJobStatus.Scheduled,
            ContentMigrationJobStatus.Running,
            ContentMigrationJobStatus.Paused,
        };

        if (!cancellableStatuses.Contains(job.Status))
            return BadRequest($"Job {jobId} cannot be cancelled in its current state: {job.Status}.");

        job.Status        = ContentMigrationJobStatus.Failed;
        job.ErrorMessage  = "Cancelled by user.";
        job.LastUpdatedAt = DateTime.UtcNow;

        await _jobs.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "CONTENT_MIGRATION_JOB_CANCELLED",
            Resource  = $"projects/{projectId}/content-migrations/{jobId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{jobId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Content migration job {JobId} cancelled for project {ProjectId}.", jobId, projectId);
        return Ok(MapToResponse(job));
    }

    /// <summary>Delete a content migration job and all its items.</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{jobId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid jobId, CancellationToken ct)
    {
        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId) return NotFound();

        if (job.Status == ContentMigrationJobStatus.Running)
            return UnprocessableEntity(new { message = "Cannot delete a running job. Cancel it first." });

        await _jobs.DeleteJobAsync(jobId, ct);
        await _jobs.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "CONTENT_JOB_DELETED",
            Resource  = $"projects/{projectId}/content-migrations/{jobId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{jobId}}}","name":"{{{job.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return NoContent();
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static ContentJobResponse MapToResponse(ContentMigrationJob j)
    {
        var pct = j.TotalItems > 0
            ? Math.Round((double)j.MigratedItems / j.TotalItems * 100, 1)
            : 0.0;

        return new ContentJobResponse(
            Id:               j.Id,
            ProjectId:        j.ProjectId,
            Name:             j.Name,
            JobType:          j.JobType.ToCamelCase(),
            Status:           j.Status.ToCamelCase(),
            TotalItems:       j.TotalItems,
            MigratedItems:    j.MigratedItems,
            FailedItems:      j.FailedItems,
            ProgressPercent:  pct,
            SpoMigrationJobId: j.SpoMigrationJobId,
            ErrorMessage:     j.ErrorMessage,
            CreatedAt:        j.CreatedAt,
            StartedAt:        j.StartedAt,
            CompletedAt:      j.CompletedAt,
            LastUpdatedAt:    j.LastUpdatedAt
        );
    }

    /// <summary>
    /// Start OneDrive provisioning for the target users of a job. Transitions the
    /// job to <c>Provisioning</c>, kicks off a background monitor, and returns
    /// immediately with <c>202 Accepted</c>. The monitor polls the target tenant
    /// until every UPN has a drive, then flips the job to <c>Ready</c> (or to
    /// <c>Failed</c> with an error if provisioning ultimately fails; timeouts keep
    /// the job in <c>Provisioning</c> and re-check later, since SPO can take hours).
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/provision-onedrive")]
    public async Task<IActionResult> ProvisionOneDrive(
        Guid projectId,
        Guid jobId,
        [FromServices] OneDriveProvisioningQueue provisioningQueue,
        [FromServices] IProgressNotifier notifier,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetJobByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        if (job.JobType != ContentMigrationJobType.OneDrive)
            return BadRequest("OneDrive provisioning is only applicable to OneDrive migration jobs.");

        if (job.Status != ContentMigrationJobStatus.Draft &&
            job.Status != ContentMigrationJobStatus.Ready &&
            job.Status != ContentMigrationJobStatus.Failed)
        {
            return BadRequest(
                $"Only Draft, Ready, or Failed jobs can be (re-)provisioned. Current status: {job.Status.ToCamelCase()}.");
        }

        var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
        if (project?.TargetTenant is null)
            return UnprocessableEntity(new { message = "Target tenant not found." });

        // Verify every target user has an active SharePoint Online (OneDrive) plan.
        // Without it, Request-SPOPersonalSite succeeds silently but no drive is ever
        // created, and the worker times out 10 minutes later with an opaque error.
        if (!_configuration.GetValue<bool>("Platform:MockGraphCalls"))
        {
            var items = (await _jobs.GetItemsByJobAsync(jobId, ct)).ToList();
            var upns = items
                .Where(i => !string.IsNullOrWhiteSpace(i.TargetOwnerUpn))
                .Select(i => i.TargetOwnerUpn!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (upns.Count > 0)
            {
                try
                {
                    var (cert, pw, secret) = await _keyVault.LoadCredentialsAsync(project.TargetTenant.Id, ct);
                    var graph = _graphFactory.CreateForTenant(project.TargetTenant, cert, pw, secret);
                    var verdicts = await _licenseCheck.CheckOneDriveLicensesAsync(graph, upns, ct);
                    var unlicensed = verdicts.Where(v => !v.HasLicense).ToList();
                    if (unlicensed.Count > 0)
                    {
                        _logger.LogWarning(
                            "ProvisionOneDrive: {Count} target user(s) on job {JobId} are missing a OneDrive license.",
                            unlicensed.Count, jobId);
                        return UnprocessableEntity(new
                        {
                            message = $"{unlicensed.Count} target user(s) are missing an active SharePoint Online / OneDrive license. Assign a OneDrive-bearing license (e.g. Microsoft 365 E3, Business Standard) and try again.",
                            unlicensedUsers = unlicensed.Select(u => new { upn = u.Upn, reason = u.Reason }),
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ProvisionOneDrive: license check failed for job {JobId} — skipping precheck.", jobId);
                }
            }
        }

        // Transition to Provisioning up front so the UI can reflect progress immediately.
        job.Status = ContentMigrationJobStatus.Provisioning;
        job.ErrorMessage = null;
        job.LastUpdatedAt = DateTime.UtcNow;
        await _jobs.SaveAsync(ct);

        try
        {
            await notifier.NotifyContentJobProgressAsync(
                job.Id, job.ProjectId,
                job.MigratedItems, job.TotalItems, job.FailedItems,
                job.Status.ToCamelCase(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ProvisionOneDrive: SignalR notify failed for job {JobId} — continuing.", job.Id);
        }

        provisioningQueue.Channel.Writer.TryWrite(jobId);

        return Accepted(new
        {
            jobId,
            status = job.Status.ToCamelCase(),
            message = "OneDrive provisioning started. The job will transition to Ready once all target users are provisioned.",
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<SpoPowerShellCredentials> BuildSpoCredentialsAsync(Tenant tenant, CancellationToken ct)
    {
        var (certB64, certPwd) = await _keyVault.LoadCertificateWithFallbackAsync(tenant, ct);
        if (string.IsNullOrEmpty(certB64))
        {
            throw new InvalidOperationException(
                $"No certificate found for tenant {tenant.Id} (checked Key Vault and the tenant record). " +
                "The SPO cross-tenant cmdlets require an app-only certificate — a client secret is not supported. " +
                "Upload the PFX via Tenants → Re-configure App.");
        }
        if (string.IsNullOrWhiteSpace(tenant.AppClientId) || string.IsNullOrWhiteSpace(tenant.TenantId))
        {
            throw new InvalidOperationException(
                $"Tenant {tenant.Id} is missing AppClientId or TenantId.");
        }
        return new SpoPowerShellCredentials(
            tenant.TenantId, tenant.AppClientId, certB64, certPwd,
            SpoPowerShellCredentials.DefaultKeyVaultCertificateName(tenant.Id));
    }

    /// <summary>
    /// Build the cross-tenant identity map CSV (Microsoft's 6-column, header-less
    /// format) from the project's mapped identities plus this job's item pairs and
    /// upload it to the TARGET tenant via <c>Add-SPOTenantIdentityMap</c>.
    /// Each upload overwrites the previous map, so the full project mapping set is
    /// always included (per Microsoft: "your identity map should always include
    /// everyone you're wanting to migrate"). Returns an error message on failure,
    /// or null on success / benign skip.
    /// </summary>
    private async Task<string?> UploadIdentityMapForProjectAsync(
        Guid projectId,
        ContentMigrationJob job,
        Tenant sourceTenant,
        string targetAdminUrl,
        SpoPowerShellCredentials targetSpoCreds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceTenant.TenantId))
        {
            return "Source tenant Entra tenant ID is not set — it is required as the " +
                   "SourceTenantCompanyID column of the SPO identity map. Re-verify the source tenant.";
        }

        // Project-level mappings (auto-map / manual / CSV import) …
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        foreach (var map in await _identityMaps.GetByProjectAsync(projectId, ct))
        {
            if (map.Status != MappingStatus.Mapped || string.IsNullOrWhiteSpace(map.TargetUpn))
                continue;
            AddMapping(mappings, conflicts, map.SourceUpn, map.TargetUpn!);
        }

        // … plus this job's explicit item pairs (authoritative for the job).
        foreach (var item in await _jobs.GetItemsByJobAsync(job.Id, ct))
        {
            if (!string.IsNullOrWhiteSpace(item.OwnerUpn) && !string.IsNullOrWhiteSpace(item.TargetOwnerUpn))
                AddMapping(mappings, conflicts, item.OwnerUpn!, item.TargetOwnerUpn!, overwrite: true);
        }

        if (conflicts.Count > 0)
        {
            return "Identity map conflict — SPO requires a one-to-one user mapping. " +
                   "Resolve these on the Identity Mapping tab and retry: " +
                   string.Join("; ", conflicts.Distinct().Take(10));
        }

        if (mappings.Count == 0)
        {
            // Site-move jobs with no recorded identity maps: don't hard-block, but
            // permissions on migrated content cannot be re-mapped without one.
            _logger.LogWarning(
                "No identity mappings found for project {ProjectId} — skipping Add-SPOTenantIdentityMap for job {JobId}. " +
                "Run Auto-Map (or import a CSV) so migrated content permissions resolve on the target.",
                projectId, job.Id);
            return null;
        }

        // One-to-one also holds in the target direction.
        var duplicateTargets = mappings
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ← {string.Join(", ", g.Select(kv => kv.Key))}")
            .ToList();
        if (duplicateTargets.Count > 0)
        {
            return "Identity map conflict — multiple source users map to the same target user, " +
                   "which SPO's one-to-one identity map does not allow: " +
                   string.Join("; ", duplicateTargets.Take(10));
        }

        // 6 columns, NO header row: User,SourceTenantCompanyID,SourceUserUpn,TargetUserUpn,TargetUserEmail,UserType
        // UserType must be "RegularUser" (per the example in Microsoft's cross-tenant
        // OneDrive migration step 5 doc). Any other value (e.g. "Member") makes
        // Add-SPOTenantIdentityMap reject the row — and it reports rejections on the
        // console WITHOUT a terminating error, so the upload appears to succeed while
        // the map stays empty and Start-SPOCrossTenantUserContentMove later fails with
        // "Identity map entry for source UPN [...] does not exist on the target tenant".
        var csv = new System.Text.StringBuilder();
        foreach (var (sourceUpn, targetUpn) in mappings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            csv.Append("User,");
            csv.Append(CsvField(sourceTenant.TenantId!)).Append(',');
            csv.Append(CsvField(sourceUpn)).Append(',');
            csv.Append(CsvField(targetUpn)).Append(',');
            csv.Append(CsvField(targetUpn)).Append(',');
            csv.Append("RegularUser\n");
        }

        var csvBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(csv.ToString()));

        try
        {
            await _spoClient.UploadIdentityMapAsync(targetAdminUrl, csvBase64, targetSpoCreds, ct);
            _logger.LogInformation(
                "Uploaded identity map with {Count} user mapping(s) to {TargetAdminUrl} for job {JobId}.",
                mappings.Count, targetAdminUrl, job.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Identity map upload failed for job {JobId} (target {TargetAdminUrl}).", job.Id, targetAdminUrl);
            return $"Failed to upload the cross-tenant identity map to the target tenant: {ex.Message} " +
                   "Content moves cannot start until Add-SPOTenantIdentityMap succeeds on the target admin endpoint.";
        }

        static void AddMapping(
            Dictionary<string, string> mappings, List<string> conflicts,
            string sourceUpn, string targetUpn, bool overwrite = false)
        {
            sourceUpn = sourceUpn.Trim();
            targetUpn = targetUpn.Trim();
            if (mappings.TryGetValue(sourceUpn, out var existing))
            {
                if (string.Equals(existing, targetUpn, StringComparison.OrdinalIgnoreCase))
                    return;
                if (overwrite)
                    mappings[sourceUpn] = targetUpn; // job item pair wins over stale project map
                else
                    conflicts.Add($"{sourceUpn} → {existing} vs {targetUpn}");
                return;
            }
            mappings[sourceUpn] = targetUpn;
        }

        static string CsvField(string value) =>
            value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
    }

    private static ContentItemResponse MapItemToResponse(ContentMigrationItem i) =>
        new(
            Id:              i.Id,
            JobId:           i.JobId,
            SourceUrl:       i.SourceUrl,
            TargetUrl:       i.TargetUrl,
            OwnerUpn:        i.OwnerUpn,
            TargetOwnerUpn:  i.TargetOwnerUpn,
            SpoJobId:        i.SpoJobId,
            Status:          i.Status.ToCamelCase(),
            ProgressPercent: i.ProgressPercent,
            ErrorMessage:    i.ErrorMessage,
            LastUpdated:     i.LastUpdated
        );
}
