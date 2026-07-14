namespace MigrationPlatform.Api.DTOs;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record CreateValidationRunRequest(
    string? Name,
    Guid? WaveId);

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record ValidationRunResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    Guid? WaveId,
    string Status,
    int TotalChecks,
    int PassedChecks,
    int FailedChecks,
    int WarningChecks,
    double ProgressPercent,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage);

public record ValidationCheckResponse(
    Guid Id,
    Guid RunId,
    string CheckType,
    string SourceReference,
    string TargetReference,
    string Outcome,
    string? ErrorMessage,
    DateTime CheckedAt);
