namespace MigrationPlatform.Api.DTOs;

public record CreateDomainCutoverRequest(string DomainName);

public record DomainCutoverResponse(
    Guid Id,
    Guid ProjectId,
    string DomainName,
    string Phase,
    int TotalUsers,
    int CompletedUsers,
    int FailedUsers,
    string? DnsVerificationRecord,
    string? TargetMxRecord,
    string? ErrorMessage,
    string? DnsInstructions,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? LastUpdatedAt
);
