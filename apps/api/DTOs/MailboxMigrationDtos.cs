namespace MigrationPlatform.Api.DTOs;

/// <summary>
/// A single mailbox pair provided when creating a migration batch.
/// </summary>
public record MailboxEntryRequest(string SourceUpn, string TargetUpn);

/// <summary>
/// Request body for creating a new mailbox migration batch.
/// </summary>
/// <param name="TargetFolderName">
/// Optional folder name in target mailbox to place copied mail under.
/// Only honoured when <paramref name="Strategy"/> is <c>graphCopy</c>; ignored
/// for native MRS (the EXO MoveRequest restores the source folder structure).
/// </param>
/// <param name="Strategy">
/// Mail transport: <c>graphCopy</c> (default, per-message Graph copy) or
/// <c>nativeMrs</c> (server-side cross-tenant MRS via EXO).
/// </param>
public record CreateMailboxBatchRequest(
    string Name,
    IReadOnlyList<MailboxEntryRequest> Mailboxes,
    string? TargetFolderName = null,
    string? Strategy = null);

/// <summary>
/// Summary response for a <see cref="Models.MailboxMigrationBatch"/>, including the
/// derived completion percentage for convenience.
/// </summary>
public record MailboxBatchResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Status,
    string Strategy,
    int TotalMailboxes,
    int SyncedMailboxes,
    int FailedMailboxes,
    int SkippedMailboxes,
    double ProgressPercent,
    string? ExoMigrationBatchId,
    string? TargetFolderName,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? LastSyncedAt,
    LicenseAssignmentSummary? LicenseAssignment = null
);

/// <summary>
/// Outcome of the automatic Cross Tenant User Data Migration license assignment
/// performed when a native-MRS batch starts. Present only on the response of
/// <c>POST /start</c> (and only when the auto-assign ran); null elsewhere.
/// </summary>
public record LicenseAssignmentSummary(
    bool Attempted,
    string Side,
    bool SkuFound,
    int SeatsAvailable,
    int Assigned,
    int AlreadyLicensed,
    int Failed,
    IReadOnlyList<LicenseAssignmentFailureDto> Failures,
    string? Warning
);

/// <summary>One UPN the license auto-assign could not license, with the reason.</summary>
public record LicenseAssignmentFailureDto(string Upn, string Reason);

/// <summary>
/// Per-tenant cleanup summary returned by <c>POST /reset-target</c> so the caller can
/// see which EXO objects were removed before the batch was reset to Draft.
/// </summary>
public record ResetMailboxBatchTargetResponse(
    Guid BatchId,
    int MoveRequestsRemoved,
    int MailUsersRemoved,
    int SoftDeletedMailUsersPurged,
    bool ExoBatchRemoved,
    int EntriesReset,
    IReadOnlyList<string> Warnings,
    MailboxBatchResponse Batch
);

/// <summary>
/// Response for a single mailbox entry within a batch.
/// </summary>
public record MailboxEntryResponse(
    Guid Id,
    Guid BatchId,
    string SourceUpn,
    string TargetUpn,
    string Status,
    double ItemsSyncedPercent,
    int MessagesCopied,
    int TotalMessages,
    string? ErrorMessage,
    DateTime? LastUpdated
);
