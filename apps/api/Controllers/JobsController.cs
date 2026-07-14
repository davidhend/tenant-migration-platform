using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobsController : ControllerBase
{
    private readonly IJobRepository _jobs;
    private readonly IScanRepository _scans;
    private readonly ScanJobQueue _queue;
    private readonly IAuditRepository _audit;
    private readonly ICurrentUserService _currentUser;

    public JobsController(
        IJobRepository jobs,
        IScanRepository scans,
        ScanJobQueue queue,
        IAuditRepository audit,
        ICurrentUserService currentUser)
    {
        _jobs = jobs;
        _scans = scans;
        _queue = queue;
        _audit = audit;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? projectId, CancellationToken ct) =>
        Ok(await _jobs.GetAllAsync(projectId, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(id, ct);
        return job is null ? NotFound() : Ok(job);
    }

    [Authorize(Policy = "Operator")]
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(id, ct);
        if (job is null) return NotFound();

        if (job.Status != JobStatus.Failed && job.Status != JobStatus.Cancelled)
            return BadRequest("Only failed or cancelled jobs can be retried.");

        // Only scan jobs have a consumer wired to this controller's queue. Resetting
        // any other type to Queued would strand it there forever — direct callers to
        // the workload-specific retry endpoint instead.
        if (job.Type != JobType.Scan || !job.ScanId.HasValue)
        {
            return UnprocessableEntity(
                $"Jobs of type '{job.Type}' cannot be retried here. Use the workload-specific " +
                "retry endpoint instead (e.g. POST /api/projects/{projectId}/mailbox-batches/{batchId}/retry " +
                "for mailbox batches, or the content-migration job retry endpoint for OneDrive/SharePoint).");
        }

        job.Status = JobStatus.Queued;
        job.Progress = 0;
        job.ErrorMessage = null;
        job.StartedAt = null;
        job.CompletedAt = null;

        var scan = await _scans.GetByIdAsync(job.ScanId.Value, ct);
        if (scan is not null)
        {
            scan.Status = ScanStatus.Queued;
            scan.Progress = 0;
            scan.ErrorMessage = null;
            await _scans.SaveAsync(ct);
        }
        _queue.Channel.Writer.TryWrite(job.ScanId.Value);

        await _jobs.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "JOB_RETRIED",
            Resource  = $"jobs/{job.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = job.ProjectId,
        }, ct);
        await _audit.SaveAsync(ct);

        return Ok(job);
    }

    [Authorize(Policy = "Operator")]
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(id, ct);
        if (job is null) return NotFound();

        if (job.Status != JobStatus.Running && job.Status != JobStatus.Queued)
            return BadRequest("Only running or queued jobs can be cancelled.");

        // Cancellation is only observed by the ScanWorker. Flipping other job types
        // to Cancelled here would lie — their workers keep running and later
        // overwrite the status. Direct callers to the workload's stop endpoint.
        if (job.Type != JobType.Scan || !job.ScanId.HasValue)
        {
            return UnprocessableEntity(
                $"Jobs of type '{job.Type}' cannot be cancelled here. Use the workload-specific " +
                "stop endpoint instead (e.g. POST /api/projects/{projectId}/mailbox-batches/{batchId}/stop).");
        }

        // Signal the ScanWorker; it flips the job to Cancelled when it observes the
        // request — immediately for queued scans, at the next cancellable await for
        // running ones. The job status is therefore NOT changed synchronously here.
        _queue.RequestCancel(job.ScanId.Value);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "JOB_CANCEL_REQUESTED",
            Resource  = $"jobs/{job.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = job.ProjectId,
        }, ct);
        await _audit.SaveAsync(ct);

        return Accepted(job);
    }
}
