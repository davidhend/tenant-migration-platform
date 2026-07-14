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
/// Orchestrates user migration batches for a project. Each batch provisions
/// member accounts in the target tenant by calling Microsoft Graph
/// <c>POST /users</c> directly (one call per source→target UPN pair).
/// Created users are regular member accounts in the target tenant — not guests.
/// Replaces the earlier Entra cross-tenant synchronisation flow.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/user-migrations")]
[Authorize]
public class UserMigrationController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly IUserMigrationRepository _batches;
    private readonly IMailboxMigrationRepository _mailboxBatches;
    private readonly IAuditRepository _audit;
    private readonly UserMigrationQueue _queue;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<UserMigrationController> _logger;

    public UserMigrationController(
        IProjectRepository projects,
        IUserMigrationRepository batches,
        IMailboxMigrationRepository mailboxBatches,
        IAuditRepository audit,
        UserMigrationQueue queue,
        ICurrentUserService currentUser,
        ILogger<UserMigrationController> logger)
    {
        _projects = projects;
        _batches  = batches;
        _mailboxBatches = mailboxBatches;
        _audit    = audit;
        _queue    = queue;
        _currentUser = currentUser;
        _logger   = logger;
    }

    /// <summary>
    /// Pure ordering check: users whose mailbox migrates must NOT go through user
    /// migration. The mailbox flow's <c>New-MailUser</c> creates the target AAD
    /// identity itself — a pre-existing member account at the same UPN makes that
    /// step fail ("existing identity"), and once the mailbox has moved, the target
    /// account already exists so provisioning would collide. Matches on source OR
    /// target UPN, case-insensitive; mailbox entries marked Skipped don't count
    /// (their mailbox is explicitly not migrating).
    /// </summary>
    internal static List<string> FindMailboxOverlaps(
        IEnumerable<UserMigrationEntry> userEntries,
        IEnumerable<MailboxMigrationEntry> mailboxEntries)
    {
        var mailboxUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mailboxEntries)
        {
            if (m.Status == MailboxMigrationStatus.Skipped) continue;
            if (!string.IsNullOrWhiteSpace(m.SourceUpn)) mailboxUpns.Add(m.SourceUpn.Trim());
            if (!string.IsNullOrWhiteSpace(m.TargetUpn)) mailboxUpns.Add(m.TargetUpn.Trim());
        }

        return userEntries
            .Where(u => u.Status is not (UserMigrationEntryStatus.Provisioned or UserMigrationEntryStatus.Skipped))
            .Where(u => (!string.IsNullOrWhiteSpace(u.SourceUpn) && mailboxUpns.Contains(u.SourceUpn.Trim())) ||
                        (!string.IsNullOrWhiteSpace(u.TargetUpn) && mailboxUpns.Contains(u.TargetUpn.Trim())))
            .Select(u => u.SourceUpn)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns a 422 blocking response when the batch contains users that overlap a
    /// mailbox migration in this project, else null. Called by every path that
    /// (re-)enqueues a user migration batch.
    /// </summary>
    private async Task<IActionResult?> CheckMailboxOrderingAsync(
        Guid projectId, Guid batchId, CancellationToken ct)
    {
        var userEntries    = await _batches.GetEntriesByBatchAsync(batchId, ct);
        var mailboxEntries = await _mailboxBatches.GetEntriesByProjectAsync(projectId, ct);
        var overlaps       = FindMailboxOverlaps(userEntries, mailboxEntries);
        if (overlaps.Count == 0) return null;

        _logger.LogWarning(
            "User migration batch {BatchId} blocked: {Count} user(s) overlap mailbox migrations ({Users}).",
            batchId, overlaps.Count, string.Join(", ", overlaps));

        return UnprocessableEntity(new
        {
            message =
                $"Blocked: {overlaps.Count} user(s) in this batch also appear in a mailbox migration batch " +
                $"({string.Join(", ", overlaps)}). The mailbox flow creates the target account itself " +
                "(MailUser provisioning) — run the mailbox batch for these users instead, and keep user " +
                "migration for users whose mailbox is not migrating. Remove the overlapping users from " +
                "this batch (or mark their mailbox entries Skipped if the mailbox really should not move).",
            conflictingUsers = overlaps,
        });
    }

    /// <summary>List all user migration batches for the given project.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var results = await _batches.GetBatchesByProjectAsync(projectId, ct);
        return Ok(results.Select(MapToResponse));
    }

    /// <summary>
    /// Create a new user migration batch in Draft status. The batch does not
    /// contact Graph until <c>POST .../start</c> is called.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateUserMigrationBatchRequest req,
        CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Batch name is required.");

        if (req.Users is null || req.Users.Count == 0)
            return BadRequest("At least one user entry is required.");

        var invalid = req.Users
            .Where(u => string.IsNullOrWhiteSpace(u.SourceUpn) || string.IsNullOrWhiteSpace(u.TargetUpn))
            .ToList();
        if (invalid.Count > 0)
            return BadRequest("All user entries must have both SourceUpn and TargetUpn.");

        UserMigrationStrategy strategy;
        try
        {
            strategy = ParseStrategy(req.Strategy);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        var batch = new UserMigrationBatch
        {
            ProjectId  = projectId,
            Name       = req.Name.Trim(),
            Status     = UserMigrationBatchStatus.Draft,
            Strategy   = strategy,
            TotalUsers = req.Users.Count,
        };

        var entries = req.Users.Select(u => new UserMigrationEntry
        {
            BatchId   = batch.Id,
            SourceUpn = u.SourceUpn.Trim(),
            TargetUpn = u.TargetUpn.Trim(),
            Status    = UserMigrationEntryStatus.Queued,
        }).ToList();

        await _batches.AddBatchAsync(batch, ct);
        await _batches.AddEntriesAsync(entries, ct);
        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_BATCH_CREATED",
            Resource  = $"projects/{projectId}/user-migrations/{batch.Id}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batch.Id}}}","name":"{{{batch.Name}}}","strategy":"{{{batch.Strategy.ToCamelCase()}}}","userCount":{{{batch.TotalUsers}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "User migration batch {BatchId} ({Name}) created for project {ProjectId} with {Count} users.",
            batch.Id, batch.Name, projectId, batch.TotalUsers);

        return CreatedAtAction(
            nameof(GetById),
            new { projectId, batchId = batch.Id },
            MapToResponse(batch));
    }

    /// <summary>Get a single user migration batch by ID.</summary>
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

    /// <summary>List all user entries within a batch.</summary>
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
    /// Start a Draft batch — transitions it to Provisioning and enqueues it for
    /// the worker to call Graph <c>POST /users</c> for each entry.
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

        if (batch.Status != UserMigrationBatchStatus.Draft)
            return BadRequest($"Only Draft batches can be started. Current status: {batch.Status}.");

        if (await CheckMailboxOrderingAsync(projectId, batchId, ct) is { } blocked)
            return blocked;

        batch.Status    = UserMigrationBatchStatus.Provisioning;
        batch.StartedAt = DateTime.UtcNow;
        await _batches.SaveAsync(ct);

        _queue.Channel.Writer.TryWrite(batchId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_BATCH_STARTED",
            Resource  = $"projects/{projectId}/user-migrations/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","totalUsers":{{{batch.TotalUsers}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "User migration batch {BatchId} started for project {ProjectId} ({Count} users).",
            batchId, projectId, batch.TotalUsers);

        return Ok(MapToResponse(batch));
    }

    /// <summary>Stop a Provisioning batch — transitions it to Stopped.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/stop")]
    public async Task<IActionResult> Stop(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Status != UserMigrationBatchStatus.Provisioning)
            return BadRequest($"Only Provisioning batches can be stopped. Current status: {batch.Status}.");

        batch.Status      = UserMigrationBatchStatus.Stopped;
        batch.CompletedAt = DateTime.UtcNow;
        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_BATCH_STOPPED",
            Resource  = $"projects/{projectId}/user-migrations/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("User migration batch {BatchId} stopped for project {ProjectId}.", batchId, projectId);
        return Ok(MapToResponse(batch));
    }

    /// <summary>
    /// Retry all failed entries in a batch — resets them to Queued and re-enqueues
    /// the batch. The batch must be in Completed or Failed state.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{batchId:guid}/retry-failed")]
    public async Task<IActionResult> RetryFailed(Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(projectId, ct))
            return NotFound($"Project {projectId} not found.");

        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId)
            return NotFound($"Batch {batchId} not found.");

        if (batch.Status is not (UserMigrationBatchStatus.Completed or UserMigrationBatchStatus.Failed))
            return BadRequest($"Only Completed or Failed batches can retry failed entries. Current status: {batch.Status}.");

        var failedEntries = await _batches.GetEntriesByBatchAndStatusAsync(
            batchId, UserMigrationEntryStatus.Failed, ct);
        if (failedEntries.Count == 0)
            return Ok(new { message = "No failed entries to retry.", batch = MapToResponse(batch) });

        if (await CheckMailboxOrderingAsync(projectId, batchId, ct) is { } blocked)
            return blocked;

        foreach (var entry in failedEntries)
        {
            entry.Status       = UserMigrationEntryStatus.Queued;
            entry.ErrorMessage = null;
            entry.LastUpdated  = DateTime.UtcNow;
        }

        batch.Status       = UserMigrationBatchStatus.Provisioning;
        batch.CompletedAt  = null;
        batch.ErrorMessage = null;

        await _batches.SaveAsync(ct);
        _queue.Channel.Writer.TryWrite(batchId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_BATCH_RETRY_FAILED",
            Resource  = $"projects/{projectId}/user-migrations/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","retriedEntries":{{{failedEntries.Count}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "User migration batch {BatchId}: retrying {Count} failed entries.",
            batchId, failedEntries.Count);

        return Ok(new { message = $"Retrying {failedEntries.Count} failed entries.", batch = MapToResponse(batch) });
    }

    /// <summary>
    /// Re-run a terminal batch from scratch. Unlike <c>retry-failed</c> (which only
    /// resets <c>Failed</c> entries), this resets every non-<c>Provisioned</c>
    /// entry — <c>Failed</c>, <c>Skipped</c>, and the leftover <c>Queued</c>/<c>Provisioning</c>
    /// entries from a <see cref="UserMigrationBatchStatus.Failed"/> batch that hit
    /// a batch-level error (e.g. a <c>CrossTenantSync</c> batch that bailed before
    /// touching any entries) — back to <c>Queued</c>, then re-enqueues.
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

        if (batch.Status is not (UserMigrationBatchStatus.Failed
                              or UserMigrationBatchStatus.Stopped
                              or UserMigrationBatchStatus.Completed))
            return BadRequest($"Only Failed, Stopped, or Completed batches can be retried. Current status: {batch.Status}.");

        if (await CheckMailboxOrderingAsync(projectId, batchId, ct) is { } blocked)
            return blocked;

        var entries = (await _batches.GetEntriesByBatchAsync(batchId, ct)).ToList();
        var resetCount = 0;
        foreach (var entry in entries)
        {
            if (entry.Status == UserMigrationEntryStatus.Provisioned) continue;

            entry.Status       = UserMigrationEntryStatus.Queued;
            entry.ErrorMessage = null;
            entry.LastUpdated  = DateTime.UtcNow;
            resetCount++;
        }

        if (resetCount == 0)
            return Ok(new { message = "Nothing to retry — every entry is already Provisioned.", batch = MapToResponse(batch) });

        batch.Status       = UserMigrationBatchStatus.Provisioning;
        batch.CompletedAt  = null;
        batch.ErrorMessage = null;
        batch.FailedUsers  = 0;
        // Full retry re-queues all non-Provisioned entries (including previously Skipped).
        // Skipped count correctly drops to 0 here — entries are counted after reset.
        batch.SkippedUsers = entries.Count(e => e.Status == UserMigrationEntryStatus.Skipped);

        await _batches.SaveAsync(ct);
        _queue.Channel.Writer.TryWrite(batchId);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_BATCH_RETRIED",
            Resource  = $"projects/{projectId}/user-migrations/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","resetEntries":{{{resetCount}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "User migration batch {BatchId}: full retry — reset {Count} entries to Queued.",
            batchId, resetCount);

        return Ok(new { message = $"Retrying batch ({resetCount} entries re-queued).", batch = MapToResponse(batch) });
    }

    /// <summary>
    /// Bulk-reclassify every currently-<c>Failed</c> entry on a batch as
    /// <c>Skipped</c>. Intended for terminal batches where the failures represent
    /// unmappable targets rather than real provisioning errors.
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
        var failed = entries.Where(e => e.Status == UserMigrationEntryStatus.Failed).ToList();
        if (failed.Count == 0)
            return Ok(MapToResponse(batch));

        foreach (var e in failed)
        {
            e.Status      = UserMigrationEntryStatus.Skipped;
            e.LastUpdated = DateTime.UtcNow;
        }

        batch.ProvisionedUsers = entries.Count(e => e.Status == UserMigrationEntryStatus.Provisioned);
        batch.FailedUsers      = entries.Count(e => e.Status == UserMigrationEntryStatus.Failed);
        batch.SkippedUsers     = entries.Count(e => e.Status == UserMigrationEntryStatus.Skipped);

        if (batch.Status is UserMigrationBatchStatus.Completed or UserMigrationBatchStatus.Failed)
        {
            var attempted = batch.TotalUsers - batch.SkippedUsers;
            batch.Status = (attempted > 0 && batch.FailedUsers == attempted)
                ? UserMigrationBatchStatus.Failed
                : UserMigrationBatchStatus.Completed;
            batch.ErrorMessage = null;
        }

        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_FAILURES_SKIPPED",
            Resource  = $"projects/{projectId}/user-migrations/{batchId}",
            Actor     = _currentUser.UserName,
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","reclassifiedCount":{{{failed.Count}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "User batch {BatchId}: reclassified {Count} failed entries as Skipped; status now {Status}.",
            batchId, failed.Count, batch.Status);

        return Ok(MapToResponse(batch));
    }

    /// <summary>Delete a user migration batch and all its entries.</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{batchId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid batchId, CancellationToken ct)
    {
        var batch = await _batches.GetBatchByIdAsync(batchId, ct);
        if (batch is null || batch.ProjectId != projectId) return NotFound();

        if (batch.Status == UserMigrationBatchStatus.Provisioning)
            return UnprocessableEntity(new { message = "Cannot delete a provisioning batch. Stop it first." });

        await _batches.DeleteBatchAsync(batchId, ct);
        await _batches.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "USER_MIGRATION_BATCH_DELETED",
            Resource  = $"projects/{projectId}/user-migrations/{batchId}",
            Actor     = User.Identity?.Name ?? "system",
            ProjectId = projectId,
            Details   = $$$"""{"batchId":"{{{batchId}}}","name":"{{{batch.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return NoContent();
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static UserMigrationBatchResponse MapToResponse(UserMigrationBatch b)
    {
        // Skipped entries are excluded from the denominator so a batch of mapped + skipped
        // users reads at 100% on success rather than partial.
        var effectiveTotal = Math.Max(0, b.TotalUsers - b.SkippedUsers);
        var processed = b.ProvisionedUsers + b.FailedUsers;
        var pct = effectiveTotal > 0
            ? Math.Round((double)processed / effectiveTotal * 100, 1)
            : (b.SkippedUsers > 0 ? 100.0 : 0.0);

        return new UserMigrationBatchResponse(
            Id:               b.Id,
            ProjectId:        b.ProjectId,
            Name:             b.Name,
            Status:           b.Status.ToCamelCase(),
            Strategy:         b.Strategy.ToCamelCase(),
            TotalUsers:       b.TotalUsers,
            ProvisionedUsers: b.ProvisionedUsers,
            FailedUsers:      b.FailedUsers,
            SkippedUsers:     b.SkippedUsers,
            ProgressPercent:  pct,
            ErrorMessage:     b.ErrorMessage,
            CreatedAt:        b.CreatedAt,
            StartedAt:        b.StartedAt,
            CompletedAt:      b.CompletedAt,
            LastUpdatedAt:    b.LastUpdatedAt
        );
    }

    /// <summary>
    /// Parse the user-supplied strategy string. Accepts wire format
    /// (<c>directGraph</c>/<c>crossTenantSync</c>) or enum names. Defaults to
    /// DirectGraph when null/blank/unrecognized — no Entra dependency.
    /// </summary>
    // Null/blank defaults to CrossTenantSync — the Microsoft-native mechanism
    // (Entra cross-tenant synchronization) is preferred; DirectGraph is the
    // explicit fallback for tenants without the sync prerequisites.
    private static UserMigrationStrategy ParseStrategy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return UserMigrationStrategy.CrossTenantSync;

        return raw.Trim().ToLowerInvariant() switch
        {
            "crosstenantsync" or "cross_tenant_sync" or "cross-tenant-sync" or "cts" or "entrasync"
                => UserMigrationStrategy.CrossTenantSync,
            // Explicit arm so unrecognized values surface as a warning rather than
            // silently defaulting to DirectGraph and hiding a future enum expansion.
            "directgraph" or "direct_graph" or "direct-graph" or "direct"
                => UserMigrationStrategy.DirectGraph,
            _ => throw new ArgumentException(
                $"Unrecognized strategy '{raw}'. Valid values: directGraph, crossTenantSync."),
        };
    }

    private static UserMigrationEntryResponse MapEntryToResponse(UserMigrationEntry e) =>
        new(
            Id:             e.Id,
            BatchId:        e.BatchId,
            SourceUpn:      e.SourceUpn,
            TargetUpn:      e.TargetUpn,
            TargetObjectId: e.TargetObjectId,
            Status:         e.Status.ToCamelCase(),
            ErrorMessage:   e.ErrorMessage,
            LastUpdated:    e.LastUpdated
        );
}
