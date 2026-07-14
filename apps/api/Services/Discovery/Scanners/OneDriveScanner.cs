using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Discovery.Scanners;

/// <summary>
/// Scans OneDrive for Business drives from the source tenant via Microsoft Graph.
/// </summary>
public class OneDriveScanner
{
    /// <summary>
    /// Maximum number of concurrent Graph drive requests to avoid hitting
    /// per-app throttle limits. Microsoft recommends no more than 4 concurrent
    /// requests per application token per user resource.
    /// </summary>
    private const int MaxConcurrency = 10;

    private const long BytesPerGb = 1_073_741_824;

    private readonly ILogger<OneDriveScanner> _logger;
    private readonly ITenantRepository _tenantRepo;
    private readonly IGraphClientFactory _graphClientFactory;
    private readonly IKeyVaultCredentialService _keyVault;

    public OneDriveScanner(
        ILogger<OneDriveScanner> logger,
        ITenantRepository tenantRepo,
        IGraphClientFactory graphClientFactory,
        IKeyVaultCredentialService keyVault)
    {
        _logger = logger;
        _tenantRepo = tenantRepo;
        _graphClientFactory = graphClientFactory;
        _keyVault = keyVault;
    }

    /// <summary>
    /// For each user that has a mailbox (and therefore likely has a OneDrive),
    /// issues a <c>GET /users/{id}/drive</c> request to retrieve drive quota
    /// information. Requests are batched with <see cref="MaxConcurrency"/>
    /// concurrency to stay within throttling limits.
    /// </summary>
    public async Task<List<ScannedOneDrive>> ScanAsync(
        Guid tenantId, Guid scanId,
        List<ScannedUser> users,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Scanning OneDrive drives via Microsoft Graph for tenant {TenantId} ({UserCount} users)",
            tenantId, users.Count);

        var tenant = await _tenantRepo.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found — cannot build Graph client.");

        // Load credentials from Key Vault (all nulls when KV is disabled — falls back to tenant model)
        var (certBase64, certPassword, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, cancellationToken);
        var graphClient = _graphClientFactory.CreateForTenant(tenant, certBase64, certPassword, secret);

        var results = new List<ScannedOneDrive>();
        var resultLock = new object();

        using var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        // Only query users who have a non-empty SourceObjectId so we can call /users/{id}/drive
        var eligibleUsers = users
            .Where(u => !string.IsNullOrWhiteSpace(u.SourceObjectId))
            .ToList();

        var tasks = eligibleUsers.Select(async user =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var drive = await graphClient.Users[user.SourceObjectId]
                    .Drive
                    .GetAsync(req =>
                    {
                        req.QueryParameters.Select = ["id", "webUrl", "quota", "lastModifiedDateTime"];
                    }, cancellationToken);

                if (drive is null)
                    return;

                var entry = new ScannedOneDrive
                {
                    ScanId = scanId,
                    OwnerUpn = user.Upn,
                    OwnerDisplayName = user.DisplayName,
                    DriveUrl = drive.WebUrl ?? string.Empty,
                    StorageUsedGb = drive.Quota?.Used.HasValue == true
                        ? Math.Round((double)drive.Quota.Used.Value / BytesPerGb, 2)
                        : 0,
                    StorageQuotaGb = drive.Quota?.Total.HasValue == true
                        ? Math.Round((double)drive.Quota.Total.Value / BytesPerGb, 2)
                        : 0,
                    LastModified = drive.LastModifiedDateTime?.UtcDateTime,
                    FileCount = 0, // Graph quota resource does not expose file count directly
                };

                lock (resultLock)
                {
                    results.Add(entry);
                }
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404 ||
                ex.Error?.Code?.Contains("itemNotFound", StringComparison.OrdinalIgnoreCase) == true ||
                ex.Error?.Message?.Contains("mysite not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                // User exists in Azure AD but has no OneDrive provisioned — skip silently
                _logger.LogDebug(
                    "No OneDrive provisioned for user {Upn} in tenant {TenantId}",
                    user.Upn, tenantId);
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex,
                    "Failed to retrieve OneDrive for user {Upn} in tenant {TenantId}: {Message}",
                    user.Upn, tenantId, ex.Message);
            }
            catch (ServiceException ex) when (ex.ResponseStatusCode == 404)
            {
                _logger.LogDebug(
                    "No OneDrive provisioned for user {Upn} in tenant {TenantId}",
                    user.Upn, tenantId);
            }
            catch (ServiceException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to retrieve OneDrive for user {Upn} in tenant {TenantId}: {Message}",
                    user.Upn, tenantId, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "OneDrive scan complete for tenant {TenantId}: {Count} drives found",
            tenantId, results.Count);

        return results;
    }
}
