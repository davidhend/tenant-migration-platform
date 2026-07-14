namespace MigrationPlatform.Api.DTOs;

/// <summary>A single source→target UPN pair provided when creating a user migration batch.</summary>
public record UserMigrationEntryRequest(string SourceUpn, string TargetUpn);

/// <summary>
/// Request body for creating a new user migration batch.
/// </summary>
/// <param name="Strategy">
/// Provisioning transport: <c>directGraph</c> (default, Graph <c>POST /users</c>)
/// or <c>crossTenantSync</c> (Entra cross-tenant sync, <c>provisionOnDemand</c>).
/// </param>
public record CreateUserMigrationBatchRequest(
    string Name,
    IReadOnlyList<UserMigrationEntryRequest> Users,
    string? Strategy = null);

/// <summary>
/// Summary response for a <see cref="Models.UserMigrationBatch"/>, including
/// the derived completion percentage.
/// </summary>
public record UserMigrationBatchResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Status,
    string Strategy,
    int TotalUsers,
    int ProvisionedUsers,
    int FailedUsers,
    int SkippedUsers,
    double ProgressPercent,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? LastUpdatedAt
);

/// <summary>Response for a single user entry within a migration batch.</summary>
public record UserMigrationEntryResponse(
    Guid Id,
    Guid BatchId,
    string SourceUpn,
    string TargetUpn,
    string? TargetObjectId,
    string Status,
    string? ErrorMessage,
    DateTime? LastUpdated
);
