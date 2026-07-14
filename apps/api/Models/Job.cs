namespace MigrationPlatform.Api.Models;

public enum JobType { Scan, IdentityMap, MailboxMigrate, SharePointMigrate, OneDriveMigrate }
public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? ScanId { get; set; }
    public JobType Type { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Progress { get; set; }
    public int ItemsTotal { get; set; }
    public int ItemsProcessed { get; set; }
    public int ItemsFailed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
