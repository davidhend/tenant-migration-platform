namespace MigrationPlatform.Api.DTOs;

public record CreateProjectRequest(string Name, Guid SourceTenantId, Guid TargetTenantId);

/// <summary>
/// A single prerequisite check result returned by GET /api/projects/{id}/dependency-check.
/// </summary>
/// <param name="Key">Machine-readable identifier (e.g. "source-credentials").</param>
/// <param name="Category">Display grouping (e.g. "Tenants", "Discovery").</param>
/// <param name="Name">Human-readable check name.</param>
/// <param name="Status">One of: "pass", "fail", "warning", "skipped".</param>
/// <param name="Detail">Optional extra context (e.g. the specific error message).</param>
/// <param name="Remediation">What the user should do to resolve a fail/warning.</param>
public record DependencyCheck(
    string Key,
    string Category,
    string Name,
    string Status,
    string? Detail,
    string? Remediation);

/// <summary>
/// Aggregated result returned by GET /api/projects/{id}/dependency-check.
/// </summary>
/// <param name="OverallStatus">Worst status across all checks: "blocked", "warning", or "ready".</param>
/// <param name="Checks">Ordered list of individual checks.</param>
public record DependencyCheckResult(
    string OverallStatus,
    IReadOnlyList<DependencyCheck> Checks);
