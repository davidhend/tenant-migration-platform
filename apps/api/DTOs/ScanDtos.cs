using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.DTOs;

public record StartScanRequest(Guid TenantId, Guid ProjectId, ScanType ScanType);

public record AutoMapResult(int Mapped, int Conflicts, int Unmapped);

public record UpdateIdentityMapRequest(string TargetUpn);
