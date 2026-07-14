namespace MigrationPlatform.Api.Models;

public enum MailboxMigrationStatus { Queued, Syncing, Synced, Failed, Skipped }

/// <summary>
/// Represents a single mailbox within a <see cref="MailboxMigrationBatch"/>.
/// Each entry tracks per-mailbox migration state, message copy progress,
/// and folder-level resumability.
/// </summary>
public class MailboxMigrationEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public MailboxMigrationBatch? Batch { get; set; }
    public string SourceUpn { get; set; } = string.Empty;
    public string TargetUpn { get; set; } = string.Empty;
    public MailboxMigrationStatus Status { get; set; } = MailboxMigrationStatus.Queued;
    public double ItemsSyncedPercent { get; set; }
    public int MessagesCopied { get; set; }
    public int TotalMessages { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? LastUpdated { get; set; }
}
