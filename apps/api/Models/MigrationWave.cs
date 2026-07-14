namespace MigrationPlatform.Api.Models;

public enum WaveStatus { Draft, Scheduled, Running, Completed, Failed, Cancelled }

/// <summary>
/// Represents a named phase of migration that groups mailbox batches and/or content
/// jobs together for coordinated, optionally time-scheduled execution.
///
/// Waves are ordered by <see cref="Order"/> and can be started immediately or
/// scheduled to start automatically at <see cref="ScheduledStartAt"/> by
/// <see cref="Workers.WaveSchedulerService"/>.
/// </summary>
public class MigrationWave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public MigrationProject? Project { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Display order within the project (1-based, lower runs first).</summary>
    public int Order { get; set; }

    public WaveStatus Status { get; set; } = WaveStatus.Draft;

    /// <summary>
    /// Optional UTC time at which the wave should be auto-started by the scheduler.
    /// Null means the wave must be started manually.
    /// </summary>
    public DateTime? ScheduledStartAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────
    public ICollection<MailboxMigrationBatch> MailboxBatches { get; set; } = new List<MailboxMigrationBatch>();
    public ICollection<ContentMigrationJob> ContentJobs { get; set; } = new List<ContentMigrationJob>();
    public ICollection<UserMigrationBatch> UserBatches { get; set; } = new List<UserMigrationBatch>();
}
