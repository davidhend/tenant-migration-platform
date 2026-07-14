using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;

namespace MigrationPlatform.Api.Services.Graph;

public sealed class OneDriveProvisioningService : IOneDriveProvisioningService
{
    private readonly ILogger<OneDriveProvisioningService> _logger;

    public OneDriveProvisioningService(ILogger<OneDriveProvisioningService> logger)
    {
        _logger = logger;
    }

    public async Task<OneDriveProvisioningResult> CheckAndProvisionAsync(
        GraphServiceClient client, string upn, CancellationToken ct)
    {
        try
        {
            var drive = await client.Users[upn].Drive.GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = new[] { "id", "quota" };
            }, ct);

            if (drive?.Id is not null)
            {
                _logger.LogDebug("OneDrive already provisioned for {Upn} (driveId: {DriveId}).", upn, drive.Id);
                return new OneDriveProvisioningResult(upn, true, drive.Id, null);
            }

            return new OneDriveProvisioningResult(upn, false, null, "Drive returned but no ID.");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            // 404 means the personal site does not yet exist. A Graph GET does
            // NOT trigger provisioning — Request-SPOPersonalSite (invoked by
            // OneDriveProvisioningWorker before polling begins) is what causes
            // SPO to actually create the site. This result simply reports that
            // the drive isn't ready yet so the worker keeps polling.
            _logger.LogDebug("OneDrive not yet provisioned for {Upn}.", upn);
            return new OneDriveProvisioningResult(upn, false, null,
                "OneDrive personal site not yet provisioned.");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            _logger.LogWarning("OneDrive check for {Upn} returned 403 — user may not have a OneDrive license.", upn);
            return new OneDriveProvisioningResult(upn, false, null,
                "Access denied. Ensure the user has a license that includes OneDrive for Business.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneDrive provisioning check failed for {Upn}.", upn);
            return new OneDriveProvisioningResult(upn, false, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<OneDriveProvisioningResult>> CheckAndProvisionBatchAsync(
        GraphServiceClient client, IEnumerable<string> upns, CancellationToken ct)
    {
        var results = new List<OneDriveProvisioningResult>();
        foreach (var upn in upns)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await CheckAndProvisionAsync(client, upn, ct));
        }
        return results;
    }
}
