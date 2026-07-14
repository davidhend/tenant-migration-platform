using Microsoft.Graph;

namespace MigrationPlatform.Api.Services.Graph;

public record OneDriveProvisioningResult(
    string Upn,
    bool IsProvisioned,
    string? DriveId,
    string? Error);

public interface IOneDriveProvisioningService
{
    Task<OneDriveProvisioningResult> CheckAndProvisionAsync(
        GraphServiceClient client, string upn, CancellationToken ct);

    Task<IReadOnlyList<OneDriveProvisioningResult>> CheckAndProvisionBatchAsync(
        GraphServiceClient client, IEnumerable<string> upns, CancellationToken ct);
}
