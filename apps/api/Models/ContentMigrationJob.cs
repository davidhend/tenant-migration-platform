namespace MigrationPlatform.Api.Models;

public enum ContentMigrationJobType { OneDrive, SharePoint }
public enum ContentMigrationJobStatus { Draft, Provisioning, Ready, Scheduled, Running, Paused, Completed, Failed }

/// <summary>
/// Represents an SharePoint/OneDrive cross-tenant content migration job.
/// A job groups a set of site or OneDrive URLs for coordinated migration and
/// tracks the overall progress of the SPO migration task submission.
/// </summary>
public class ContentMigrationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public MigrationProject? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public ContentMigrationJobType JobType { get; set; }
    public ContentMigrationJobStatus Status { get; set; } = ContentMigrationJobStatus.Draft;
    public int TotalItems { get; set; }
    public int MigratedItems { get; set; }
    public int FailedItems { get; set; }

    /// <summary>
    /// The SPO migration job identifier assigned by the SharePoint Migration API once
    /// the job has been submitted. Null until the job is started.
    /// </summary>
    public string? SpoMigrationJobId { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>Optional wave this job belongs to. Null for unassigned jobs.</summary>
    public Guid? WaveId { get; set; }
    public MigrationWave? Wave { get; set; }
}
