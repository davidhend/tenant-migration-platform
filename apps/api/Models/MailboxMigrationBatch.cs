namespace MigrationPlatform.Api.Models;

// Synced = initial sync done, awaiting cutover (native MRS parks here until /complete).
// Appended last so persisted integer values of pre-existing rows stay valid.
public enum BatchStatus { Draft, Validating, Syncing, Completing, Completed, Failed, Stopped, Synced }

/// <summary>
/// Selects the underlying transport for moving mail.
/// <para><c>GraphCopy</c> — per-message copy via Microsoft Graph. Zero EXO
/// infra setup, but slow (1–3 msg/sec/user) and lossy (no rules, no recoverable
/// items, no folder permissions). Best for &lt;10 GB/user.</para>
/// <para><c>NativeMrs</c> — native cross-tenant mailbox migration via the EXO
/// Mailbox Replication Service. Requires org relationship + migration endpoint
/// + MailUser stubs in the target. Server-side, full fidelity, ~1–2 GB/hour.
/// Best for &gt;10 GB/user or large batches.</para>
/// </summary>
public enum MailboxMigrationStrategy { GraphCopy, NativeMrs }

/// <summary>
/// Represents a mailbox migration batch that moves mail content from source
/// to target tenant. The transport is selected per batch via
/// <see cref="Strategy"/> — see <see cref="MailboxMigrationStrategy"/> for
/// the tradeoffs between Graph copy and native MRS.
/// </summary>
public class MailboxMigrationBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public MigrationProject? Project { get; set; }
    public string Name { get; set; } = string.Empty;
    public BatchStatus Status { get; set; } = BatchStatus.Draft;
    public int TotalMailboxes { get; set; }
    public int SyncedMailboxes { get; set; }
    public int FailedMailboxes { get; set; }

    /// <summary>
    /// Entries that were never attempted (e.g. unmappable target). Excluded from
    /// both the denominator for progress and from the "all failed → Failed" rule
    /// so the batch status reflects only mailboxes that were actually migrated.
    /// </summary>
    public int SkippedMailboxes { get; set; }

    /// <summary>Selected transport — GraphCopy (default) or NativeMrs.</summary>
    public MailboxMigrationStrategy Strategy { get; set; } = MailboxMigrationStrategy.GraphCopy;

    /// <summary>EXO migration-batch identity returned by New-MigrationBatch. Set only when <see cref="Strategy"/> is NativeMrs.</summary>
    public string? ExoMigrationBatchId { get; set; }

    /// <summary>
    /// Optional target folder name where copied mail will be placed.
    /// When set, all source folders are created as children of this folder
    /// in the target mailbox. When null, mail is copied into the target
    /// user's matching well-known folders (Inbox→Inbox, etc.) with custom
    /// folders created at root level.
    /// </summary>
    public string? TargetFolderName { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Optional wave this batch belongs to. Null for unassigned batches.</summary>
    public Guid? WaveId { get; set; }
    public MigrationWave? Wave { get; set; }
}
