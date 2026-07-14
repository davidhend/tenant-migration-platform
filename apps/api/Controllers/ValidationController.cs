using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Manages post-migration validation runs for a project.
///
/// A validation run loads all completed mailbox batches (Status=Completed) and
/// content migration jobs (Status=Completed), then verifies that each migrated
/// object exists and is accessible in the target tenant.
///
/// Each check calls the target tenant's Microsoft Graph and SharePoint APIs via
/// <c>IGraphClientFactory</c> — integration not yet implemented;
/// <see cref="Workers.ValidationWorker"/> marks runs Failed until wired.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/validations")]
[Authorize]
public class ValidationController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IValidationRepository _validations;
    private readonly IAuditRepository _audit;
    private readonly ValidationQueue _queue;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ValidationController> _logger;

    public ValidationController(
        IProjectRepository projects,
        IValidationRepository validations,
        IAuditRepository audit,
        ValidationQueue queue,
        ICurrentUserService currentUser,
        ILogger<ValidationController> logger)
    {
        _projects    = projects;
        _validations = validations;
        _audit       = audit;
        _queue       = queue;
        _currentUser = currentUser;
        _logger      = logger;
    }

    /// <summary>List all validation runs for the project, newest first.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var runs = await _validations.GetRunsByProjectAsync(projectId, ct);
        return Ok(runs.Select(MapToResponse));
    }

    /// <summary>Get a single validation run with its checks.</summary>
    [HttpGet("{runId:guid}")]
    public async Task<IActionResult> GetById(Guid projectId, Guid runId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var run = await _validations.GetRunByIdAsync(runId, ct);
        if (run is null || run.ProjectId != projectId)
            return NotFound($"Validation run {runId} not found.");

        return Ok(MapToResponse(run));
    }

    /// <summary>Get all checks for a validation run.</summary>
    [HttpGet("{runId:guid}/checks")]
    public async Task<IActionResult> GetChecks(Guid projectId, Guid runId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var run = await _validations.GetRunWithChecksAsync(runId, ct);
        if (run is null || run.ProjectId != projectId)
            return NotFound($"Validation run {runId} not found.");

        return Ok(run.Checks.Select(MapCheckToResponse));
    }

    /// <summary>
    /// Start a new validation run. Enqueues the run for async processing by
    /// <see cref="Workers.ValidationWorker"/>. Returns immediately with the run
    /// in Pending status; use GET to poll progress or subscribe to SignalR for
    /// real-time updates.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateValidationRunRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var name = string.IsNullOrWhiteSpace(req.Name)
            ? $"Validation Run — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
            : req.Name.Trim();

        var run = new ValidationRun
        {
            ProjectId = projectId,
            Name      = name,
            WaveId    = req.WaveId,
            Status    = ValidationRunStatus.Pending,
        };

        await _validations.AddRunAsync(run, ct);
        await _validations.SaveAsync(ct);

        // Enqueue for the background worker
        _queue.Channel.Writer.TryWrite(run.Id);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "VALIDATION_RUN_CREATED",
            Resource  = $"projects/{projectId}/validations/{run.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"runId":"{{{run.Id}}}","name":"{{{run.Name}}}","waveId":"{{{run.WaveId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Validation run {RunId} ({Name}) created for project {ProjectId}.", run.Id, run.Name, projectId);

        return CreatedAtAction(nameof(GetById), new { projectId, runId = run.Id }, MapToResponse(run));
    }

    /// <summary>
    /// Delete a validation run and all of its checks. Active runs (Pending or
    /// Running) cannot be deleted — they must finish or fail first to avoid
    /// the worker writing back to a missing row.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{runId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid runId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var run = await _validations.GetRunByIdAsync(runId, ct);
        if (run is null || run.ProjectId != projectId)
            return NotFound($"Validation run {runId} not found.");

        if (run.Status is ValidationRunStatus.Pending or ValidationRunStatus.Running)
            return UnprocessableEntity(new { message = "Cannot delete a pending or running validation. Wait for it to finish." });

        await _validations.DeleteRunAsync(runId, ct);
        await _validations.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "VALIDATION_RUN_DELETED",
            Resource  = $"projects/{projectId}/validations/{runId}",
            Actor     = User.Identity?.Name ?? _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"runId":"{{{runId}}}","name":"{{{run.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Validation run {RunId} ({Name}) deleted from project {ProjectId}.", runId, run.Name, projectId);

        return NoContent();
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static ValidationRunResponse MapToResponse(ValidationRun r)
    {
        var checked_ = r.PassedChecks + r.FailedChecks + r.WarningChecks;
        var pct = r.TotalChecks > 0
            ? Math.Round((double)checked_ / r.TotalChecks * 100, 1)
            : 0.0;

        return new ValidationRunResponse(
            Id:              r.Id,
            ProjectId:       r.ProjectId,
            Name:            r.Name,
            WaveId:          r.WaveId,
            Status:          r.Status.ToCamelCase(),
            TotalChecks:     r.TotalChecks,
            PassedChecks:    r.PassedChecks,
            FailedChecks:    r.FailedChecks,
            WarningChecks:   r.WarningChecks,
            ProgressPercent: pct,
            CreatedAt:       r.CreatedAt,
            StartedAt:       r.StartedAt,
            CompletedAt:     r.CompletedAt,
            ErrorMessage:    r.ErrorMessage);
    }

    private static ValidationCheckResponse MapCheckToResponse(ValidationCheck c) =>
        new(
            Id:              c.Id,
            RunId:           c.RunId,
            CheckType:       c.CheckType.ToCamelCase(),
            SourceReference: c.SourceReference,
            TargetReference: c.TargetReference,
            Outcome:         c.Outcome.ToCamelCase(),
            ErrorMessage:    c.ErrorMessage,
            CheckedAt:       c.CheckedAt);
}
