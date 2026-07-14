using Microsoft.Graph;
using Microsoft.Graph.Models;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Discovery.Scanners;

/// <summary>
/// Scans groups from the source tenant via Microsoft Graph.
/// </summary>
public class GroupScanner
{
    private readonly ILogger<GroupScanner> _logger;
    private readonly ITenantRepository _tenantRepo;
    private readonly IGraphClientFactory _graphClientFactory;
    private readonly IKeyVaultCredentialService _keyVault;

    public GroupScanner(
        ILogger<GroupScanner> logger,
        ITenantRepository tenantRepo,
        IGraphClientFactory graphClientFactory,
        IKeyVaultCredentialService keyVault)
    {
        _logger = logger;
        _tenantRepo = tenantRepo;
        _graphClientFactory = graphClientFactory;
        _keyVault = keyVault;
    }

    public async Task<List<ScannedGroup>> ScanAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scanning groups via Microsoft Graph for tenant {TenantId}", tenantId);

        var tenant = await _tenantRepo.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found — cannot build Graph client.");

        // Load credentials from Key Vault (all nulls when KV is disabled — falls back to tenant model)
        var (certBase64, certPassword, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, cancellationToken);
        var graphClient = _graphClientFactory.CreateForTenant(tenant, certBase64, certPassword, secret);

        var results = new List<ScannedGroup>();

        try
        {
            // ConsistencyLevel: eventual is required to use $count on groups
            var response = await graphClient.Groups.GetAsync(req =>
            {
                req.QueryParameters.Select =
                [
                    "id", "displayName", "mail", "groupTypes",
                    "membershipRule", "mailEnabled", "securityEnabled"
                ];
                req.QueryParameters.Top = 999;
                // Request member count via $count=true (requires ConsistencyLevel: eventual)
                req.QueryParameters.Count = true;
                req.Headers.Add("ConsistencyLevel", "eventual");
            }, cancellationToken);

            var pageIterator = PageIterator<Group, GroupCollectionResponse>.CreatePageIterator(
                graphClient,
                response!,
                group =>
                {
                    results.Add(MapGroup(group, scanId));
                    return true;
                });

            await pageIterator.IterateAsync(cancellationToken);
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex,
                "Microsoft Graph returned an error while scanning groups for tenant {TenantId}: {Message}",
                tenantId, ex.Message);
            throw;
        }

        // Fetch member counts in batches — individual /members/$count calls
        await EnrichMemberCountsAsync(graphClient, results, tenantId, cancellationToken);

        _logger.LogInformation(
            "Group scan complete for tenant {TenantId}: {Count} groups found",
            tenantId, results.Count);

        return results;
    }

    /// <summary>
    /// Issues individual <c>GET /groups/{id}/members/$count</c> requests with a
    /// semaphore-bounded concurrency of 5 to stay within Graph throttling limits.
    /// </summary>
    private async Task EnrichMemberCountsAsync(
        GraphServiceClient graphClient,
        List<ScannedGroup> groups,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(5, 5);

        var tasks = groups.Select(async group =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // SourceObjectId was set in MapGroup from group.Id
                var count = await graphClient.Groups[group.SourceObjectId]
                    .Members
                    .Count
                    .GetAsync(req =>
                    {
                        req.Headers.Add("ConsistencyLevel", "eventual");
                    }, cancellationToken);

                group.MemberCount = (int)(count ?? 0);
            }
            catch (ServiceException ex)
            {
                // Non-fatal: log and leave MemberCount at 0
                _logger.LogWarning(ex,
                    "Failed to retrieve member count for group {GroupId} in tenant {TenantId}",
                    group.SourceObjectId, tenantId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private static ScannedGroup MapGroup(Group group, Guid scanId) => new()
    {
        ScanId = scanId,
        SourceObjectId = group.Id ?? string.Empty,
        DisplayName = group.DisplayName ?? string.Empty,
        MailEnabled = group.MailEnabled ?? false,
        SecurityEnabled = group.SecurityEnabled ?? false,
        // Derive a human-readable type from groupTypes array
        GroupType = DeriveGroupType(group),
        MemberCount = 0, // populated by EnrichMemberCountsAsync
    };

    /// <summary>
    /// Derives a display-friendly group type: <c>"Microsoft 365"</c>, <c>"Security"</c>,
    /// or <c>"Distribution"</c>.
    /// </summary>
    private static string DeriveGroupType(Group group)
    {
        var types = group.GroupTypes ?? [];

        if (types.Contains("Unified"))
            return "Microsoft 365";

        if (group.SecurityEnabled == true && group.MailEnabled == false)
            return "Security";

        return "Distribution";
    }
}
