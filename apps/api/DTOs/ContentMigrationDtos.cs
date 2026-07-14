namespace MigrationPlatform.Api.DTOs;

/// <summary>
/// A single URL pair (source → target) provided when creating a content migration job.
/// </summary>
// SourceUrl/TargetUrl are the site URLs for SharePoint jobs (required there, validated
// per job-type in the controller) but optional metadata for OneDrive jobs, where the
// meaningful fields are OwnerUpn/TargetOwnerUpn. All nullable so OneDrive jobs — the
// common case — don't have to send dummy URLs.
public record ContentItemRequest(string? SourceUrl = null, string? TargetUrl = null, string? OwnerUpn = null, string? TargetOwnerUpn = null);

/// <summary>
/// Request body for creating a new OneDrive or SharePoint content migration job.
/// </summary>
public record CreateContentJobRequest(
    string Name,
    string JobType,
    IReadOnlyList<ContentItemRequest> Items);

/// <summary>
/// Request body for updating a Draft content migration job (name and/or items).
/// </summary>
public record UpdateContentJobRequest(
    string? Name,
    IReadOnlyList<ContentItemRequest>? Items);

/// <summary>
/// Summary response for a <see cref="Models.ContentMigrationJob"/>, including the
/// derived completion percentage for convenience.
/// </summary>
public record ContentJobResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string JobType,
    string Status,
    int TotalItems,
    int MigratedItems,
    int FailedItems,
    double ProgressPercent,
    string? SpoMigrationJobId,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? LastUpdatedAt
);

/// <summary>
/// Response for a single content item within a job.
/// </summary>
public record ContentItemResponse(
    Guid Id,
    Guid JobId,
    string SourceUrl,
    string TargetUrl,
    string? OwnerUpn,
    string? TargetOwnerUpn,
    string? SpoJobId,
    string Status,
    double ProgressPercent,
    string? ErrorMessage,
    DateTime? LastUpdated
);
