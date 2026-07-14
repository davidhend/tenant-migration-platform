namespace MigrationPlatform.Api.DTOs;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record CreateWaveRequest(
    string Name,
    string? Description,
    int Order,
    DateTime? ScheduledStartAt);

public record UpdateWaveRequest(
    string Name,
    string? Description,
    int Order,
    DateTime? ScheduledStartAt);

public record AssignBatchesToWaveRequest(IReadOnlyList<Guid> BatchIds);

public record AssignJobsToWaveRequest(IReadOnlyList<Guid> JobIds);

public record AssignUserBatchesToWaveRequest(IReadOnlyList<Guid> BatchIds);

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record WaveResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    int Order,
    string Status,
    DateTime? ScheduledStartAt,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<WaveBatchSummary> MailboxBatches,
    IReadOnlyList<WaveJobSummary> ContentJobs,
    IReadOnlyList<WaveUserBatchSummary> UserBatches);

public record WaveBatchSummary(
    Guid Id,
    string Name,
    string Status,
    int TotalMailboxes,
    int SyncedMailboxes,
    int FailedMailboxes,
    double ProgressPercent);

public record WaveJobSummary(
    Guid Id,
    string Name,
    string JobType,
    string Status,
    int TotalItems,
    int MigratedItems,
    int FailedItems,
    double ProgressPercent);

public record WaveUserBatchSummary(
    Guid Id,
    string Name,
    string Status,
    int TotalUsers,
    int ProvisionedUsers,
    int FailedUsers,
    double ProgressPercent);
