using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ScansController : ControllerBase
{
    private readonly IScanRepository _scans;
    private readonly IJobRepository _jobs;
    private readonly IAuditRepository _audit;
    private readonly ScanJobQueue _queue;
    private readonly ICurrentUserService _currentUser;

    public ScansController(
        IScanRepository scans,
        IJobRepository jobs,
        IAuditRepository audit,
        ScanJobQueue queue,
        ICurrentUserService currentUser)
    {
        _scans = scans;
        _jobs = jobs;
        _audit = audit;
        _queue = queue;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? projectId, CancellationToken ct) =>
        Ok(await _scans.GetAllAsync(projectId, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var scan = await _scans.GetByIdAsync(id, ct);
        return scan is null ? NotFound() : Ok(scan);
    }

    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> StartScan([FromBody] StartScanRequest req, CancellationToken ct)
    {
        var scan = new Scan
        {
            TenantId = req.TenantId,
            ProjectId = req.ProjectId,
            ScanType = req.ScanType,
            Status = ScanStatus.Queued,
        };

        var job = new Job
        {
            ProjectId = req.ProjectId,
            ScanId = scan.Id,
            Type = JobType.Scan,
            Status = JobStatus.Queued,
        };

        await _scans.AddAsync(scan, ct);
        await _jobs.AddAsync(job, ct);
        // Persist both scan and job in one save
        await _scans.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "SCAN_STARTED",
            Resource = $"scans/{scan.Id}",
            Actor = _currentUser.UserName,
            ProjectId = req.ProjectId,
        }, ct);
        await _audit.SaveAsync(ct);

        // Enqueue for background processing after the DB records are committed
        _queue.Channel.Writer.TryWrite(scan.Id);

        return CreatedAtAction(nameof(GetById), new { id = scan.Id }, scan);
    }

    [HttpGet("{id:guid}/users")]
    public async Task<IActionResult> GetUsers(Guid id, CancellationToken ct) =>
        Ok(await _scans.GetUsersAsync(id, ct));

    [HttpGet("{id:guid}/groups")]
    public async Task<IActionResult> GetGroups(Guid id, CancellationToken ct) =>
        Ok(await _scans.GetGroupsAsync(id, ct));

    [HttpGet("{id:guid}/mailboxes")]
    public async Task<IActionResult> GetMailboxes(Guid id, CancellationToken ct) =>
        Ok(await _scans.GetMailboxesAsync(id, ct));

    [HttpGet("{id:guid}/sites")]
    public async Task<IActionResult> GetSites(Guid id, CancellationToken ct) =>
        Ok(await _scans.GetSitesAsync(id, ct));

    [HttpGet("{id:guid}/onedrive")]
    public async Task<IActionResult> GetOneDrive(Guid id, CancellationToken ct) =>
        Ok(await _scans.GetOneDriveAsync(id, ct));

    [HttpGet("{id:guid}/domains")]
    public async Task<IActionResult> GetDomains(Guid id, CancellationToken ct) =>
        Ok(await _scans.GetDomainsAsync(id, ct));

    [HttpGet("{id:guid}/issues")]
    public async Task<IActionResult> GetIssues(Guid id, [FromQuery] string? severity, CancellationToken ct)
    {
        var issues = await _scans.GetIssuesAsync(id, ct);

        if (!string.IsNullOrEmpty(severity) && Enum.TryParse<IssueSeverity>(severity, true, out var sev))
            issues = issues.Where(i => i.Severity == sev).ToList();

        return Ok(issues.OrderBy(i => i.Severity));
    }
}
