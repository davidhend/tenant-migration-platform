namespace MigrationPlatform.Api.Models;

public enum ContentMigrationItemStatus { Queued, Running, Completed, Failed, Skipped }

/// <summary>
/// Represents a single site or OneDrive URL pair within a <see cref="ContentMigrationJob"/>.
/// Each item tracks the per-URL migration state and progress percentage as reported by
/// the SharePoint Migration API (or simulated in mock mode).
/// </summary>
public class ContentMigrationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public ContentMigrationJob? Job { get; set; }

    /// <summary>Source site or OneDrive URL in the source tenant.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Target site or OneDrive URL in the target tenant.</summary>
    public string TargetUrl { get; set; } = string.Empty;

    /// <summary>UPN of the OneDrive owner on the SOURCE tenant; null for SharePoint site items.</summary>
    public string? OwnerUpn { get; set; }

    /// <summary>UPN of the corresponding user on the TARGET tenant (required for OneDrive jobs).</summary>
    public string? TargetOwnerUpn { get; set; }

    /// <summary>
    /// The SPO migration job ID assigned to this specific item when the job is started.
    /// Enables per-item status polling and error correlation.
    /// </summary>
    public string? SpoJobId { get; set; }

    public ContentMigrationItemStatus Status { get; set; } = ContentMigrationItemStatus.Queued;
    public double ProgressPercent { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? LastUpdated { get; set; }
}
