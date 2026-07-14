namespace MigrationPlatform.Api.Models;

public enum ValidationRunStatus { Pending, Running, Completed, Failed }

/// <summary>
/// Represents a post-migration validation run for a project.
///
/// A run queries all completed mailbox batches and content jobs (optionally scoped to
/// a single wave), then checks that each migrated object is accessible in the target
/// tenant.  Results are stored as <see cref="ValidationCheck"/> records.
///
/// Each check calls the target tenant's Microsoft Graph and SharePoint APIs via
/// <c>IGraphClientFactory</c> (integration not yet implemented).
/// </summary>
public class ValidationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public MigrationProject? Project { get; set; }

    /// <summary>Human-readable label for this run (e.g. "Wave 1 validation — 2026-03-15").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional wave scope.  When set only completed batches/jobs belonging to this
    /// wave are included.  When null all completed batches/jobs in the project are validated.
    /// </summary>
    public Guid? WaveId { get; set; }

    public ValidationRunStatus Status { get; set; } = ValidationRunStatus.Pending;

    public int TotalChecks { get; set; }
    public int PassedChecks { get; set; }
    public int FailedChecks { get; set; }
    public int WarningChecks { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────
    public ICollection<ValidationCheck> Checks { get; set; } = new List<ValidationCheck>();
}
