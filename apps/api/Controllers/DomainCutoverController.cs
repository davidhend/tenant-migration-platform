using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Manages domain cutover jobs that move a custom domain from the source tenant
/// to the target tenant and reassign it to migrated users.
///
/// The cutover is a multi-phase workflow with pause points where the admin must
/// make DNS changes (TXT verification record, MX/SPF/DKIM records).
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/domain-cutover")]
[Authorize]
public class DomainCutoverController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IDomainCutoverRepository _jobs;
    private readonly IAuditRepository _audit;
    private readonly DomainCutoverQueue _queue;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<DomainCutoverController> _logger;

    public DomainCutoverController(
        IProjectRepository projects,
        IDomainCutoverRepository jobs,
        IAuditRepository audit,
        DomainCutoverQueue queue,
        ICurrentUserService currentUser,
        ILogger<DomainCutoverController> logger)
    {
        _projects = projects;
        _jobs = jobs;
        _audit = audit;
        _queue = queue;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>List all domain cutover jobs for the project.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var jobs = await _jobs.GetByProjectAsync(projectId, ct);
        return Ok(jobs.Select(MapToResponse));
    }

    /// <summary>Get a single domain cutover job.</summary>
    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetById(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var job = await _jobs.GetByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId)
            return NotFound($"Job {jobId} not found.");

        return Ok(MapToResponse(job));
    }

    /// <summary>Create a new domain cutover job in Created phase.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId, [FromBody] CreateDomainCutoverRequest req, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        if (string.IsNullOrWhiteSpace(req.DomainName))
            return BadRequest("DomainName is required.");

        var job = new DomainCutoverJob
        {
            ProjectId  = projectId,
            DomainName = req.DomainName.Trim().ToLowerInvariant(),
            Phase      = DomainCutoverPhase.Created,
        };

        await _jobs.AddAsync(job, ct);
        await _jobs.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "DOMAIN_CUTOVER_CREATED",
            Resource  = $"projects/{projectId}/domain-cutover/{job.Id}",
            Actor     = User.Identity?.Name ?? _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{job.Id}}}","domain":"{{{job.DomainName}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Domain cutover job {JobId} created for domain '{Domain}' in project {ProjectId}.",
            job.Id, job.DomainName, projectId);

        return CreatedAtAction(nameof(GetById), new { projectId, jobId = job.Id }, MapToResponse(job));
    }

    /// <summary>
    /// Start the domain cutover — transitions from Created to CleaningSource and
    /// enqueues for the background worker.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/start")]
    public async Task<IActionResult> Start(Guid projectId, Guid jobId, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId) return NotFound();

        if (job.Phase != DomainCutoverPhase.Created)
            return BadRequest($"Can only start a job in 'created' phase. Current: {job.Phase.ToCamelCase()}.");

        job.Phase = DomainCutoverPhase.CleaningSource;
        job.StartedAt = DateTime.UtcNow;
        job.LastUpdatedAt = DateTime.UtcNow;

        await _jobs.SaveAsync(ct);
        _queue.Channel.Writer.TryWrite(jobId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "DOMAIN_CUTOVER_STARTED",
            Resource  = $"projects/{projectId}/domain-cutover/{jobId}",
            Actor     = User.Identity?.Name ?? _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"jobId":"{{{jobId}}}","domain":"{{{job.DomainName}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("Domain cutover job {JobId} started.", jobId);
        return Ok(MapToResponse(job));
    }

    /// <summary>
    /// Continue the cutover after an admin pause point (DNS verification or MX update).
    /// Advances from AwaitingDnsVerification → VerifyingDomain,
    /// or from AwaitingMxUpdate → Completed.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{jobId:guid}/continue")]
    public async Task<IActionResult> Continue(Guid projectId, Guid jobId, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId) return NotFound();

        if (job.Phase == DomainCutoverPhase.AwaitingDnsVerification)
        {
            job.Phase = DomainCutoverPhase.VerifyingDomain;
            job.LastUpdatedAt = DateTime.UtcNow;
            await _jobs.SaveAsync(ct);
            _queue.Channel.Writer.TryWrite(jobId);

            _logger.LogInformation("Domain cutover job {JobId}: admin confirmed DNS verification record added.", jobId);
            return Ok(MapToResponse(job));
        }

        if (job.Phase == DomainCutoverPhase.AwaitingMxUpdate)
        {
            job.Phase = DomainCutoverPhase.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.LastUpdatedAt = DateTime.UtcNow;
            await _jobs.SaveAsync(ct);

            await _audit.AddAsync(new AuditEvent
            {
                Action    = "DOMAIN_CUTOVER_COMPLETED",
                Resource  = $"projects/{projectId}/domain-cutover/{jobId}",
                Actor     = User.Identity?.Name ?? _currentUser.UserName,
                ProjectId = projectId,
                Details   = $$$"""{"jobId":"{{{jobId}}}","domain":"{{{job.DomainName}}}"}""",
            }, ct);
            await _audit.SaveAsync(ct);

            _logger.LogInformation("Domain cutover job {JobId} completed.", jobId);
            return Ok(MapToResponse(job));
        }

        return BadRequest($"Job is in phase '{job.Phase.ToCamelCase()}' which does not support continue.");
    }

    /// <summary>Delete a domain cutover job (only Created/Completed/Failed).</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{jobId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid jobId, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct);
        if (job is null || job.ProjectId != projectId) return NotFound();

        var deletable = new[] { DomainCutoverPhase.Created, DomainCutoverPhase.Completed, DomainCutoverPhase.Failed,
                                DomainCutoverPhase.AwaitingDnsVerification, DomainCutoverPhase.AwaitingMxUpdate };
        if (!deletable.Contains(job.Phase))
            return UnprocessableEntity(new { message = $"Cannot delete a job in phase '{job.Phase}'." });

        await _jobs.DeleteAsync(jobId, ct);
        await _jobs.SaveAsync(ct);
        return NoContent();
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static DomainCutoverResponse MapToResponse(DomainCutoverJob j)
    {
        string? dnsInstructions = null;

        if (j.Phase == DomainCutoverPhase.AwaitingDnsVerification && j.DnsVerificationRecord is not null)
        {
            dnsInstructions =
                $"Add the following TXT record to the DNS zone for '{j.DomainName}':\n\n" +
                $"  Type: TXT\n  Host: @ (or {j.DomainName})\n  Value: {j.DnsVerificationRecord}\n\n" +
                "After adding the record, wait a few minutes for DNS propagation, then click 'Continue'.";
        }
        else if (j.Phase == DomainCutoverPhase.AwaitingMxUpdate)
        {
            dnsInstructions =
                $"Update the following DNS records for '{j.DomainName}':\n\n" +
                $"  MX: {j.TargetMxRecord ?? $"{j.DomainName.Replace('.', '-')}.mail.protection.outlook.com"} (priority 0)\n" +
                $"  TXT (SPF): v=spf1 include:spf.protection.outlook.com -all\n" +
                $"  CNAME: autodiscover.{j.DomainName} → autodiscover.outlook.com\n\n" +
                "After updating DNS, click 'Continue' to mark the cutover as complete.";
        }

        return new DomainCutoverResponse(
            Id:                    j.Id,
            ProjectId:             j.ProjectId,
            DomainName:            j.DomainName,
            Phase:                 j.Phase.ToCamelCase(),
            TotalUsers:            j.TotalUsers,
            CompletedUsers:        j.CompletedUsers,
            FailedUsers:           j.FailedUsers,
            DnsVerificationRecord: j.DnsVerificationRecord,
            TargetMxRecord:        j.TargetMxRecord,
            ErrorMessage:          j.ErrorMessage,
            DnsInstructions:       dnsInstructions,
            CreatedAt:             j.CreatedAt,
            StartedAt:             j.StartedAt,
            CompletedAt:           j.CompletedAt,
            LastUpdatedAt:         j.LastUpdatedAt
        );
    }
}
