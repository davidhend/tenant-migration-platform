using Microsoft.Graph;
using Microsoft.Graph.Models;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Discovery.Scanners;

/// <summary>
/// Scans SharePoint Online sites from the source tenant via Microsoft Graph.
/// </summary>
public class SharePointScanner
{
    private readonly ILogger<SharePointScanner> _logger;
    private readonly ITenantRepository _tenantRepo;
    private readonly IGraphClientFactory _graphClientFactory;
    private readonly IKeyVaultCredentialService _keyVault;

    public SharePointScanner(
        ILogger<SharePointScanner> logger,
        ITenantRepository tenantRepo,
        IGraphClientFactory graphClientFactory,
        IKeyVaultCredentialService keyVault)
    {
        _logger = logger;
        _tenantRepo = tenantRepo;
        _graphClientFactory = graphClientFactory;
        _keyVault = keyVault;
    }

    public async Task<List<ScannedSite>> ScanAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Scanning SharePoint sites via Microsoft Graph for tenant {TenantId}", tenantId);

        var tenant = await _tenantRepo.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found — cannot build Graph client.");

        // Load credentials from Key Vault (all nulls when KV is disabled — falls back to tenant model)
        var (certBase64, certPassword, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, cancellationToken);
        var graphClient = _graphClientFactory.CreateForTenant(tenant, certBase64, certPassword, secret);

        var results = new List<ScannedSite>();

        try
        {
            // getAllSites enumerates all SharePoint site collections in the tenant.
            // Requires Sites.Read.All application permission.
            var response = await graphClient.Sites.GetAllSites.GetAsGetAllSitesGetResponseAsync(req =>
            {
                req.QueryParameters.Select = ["id", "displayName", "webUrl", "siteCollection"];
                req.QueryParameters.Top = 200;
            }, cancellationToken);

            var pageIterator = PageIterator<Site, Microsoft.Graph.Sites.GetAllSites.GetAllSitesGetResponse>.CreatePageIterator(
                graphClient,
                response!,
                site =>
                {
                    // Skip OneDrive personal sites — those are handled by OneDriveScanner
                    if (site.WebUrl is not null && site.WebUrl.Contains("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase))
                        return true;

                    results.Add(MapSite(site, scanId));
                    return true;
                });

            await pageIterator.IterateAsync(cancellationToken);
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex,
                "Microsoft Graph returned an error while scanning SharePoint sites for tenant {TenantId}: {Message}",
                tenantId, ex.Message);
            throw;
        }

        _logger.LogInformation(
            "SharePoint scan complete for tenant {TenantId}: {Count} sites found",
            tenantId, results.Count);

        return results;
    }

    private static ScannedSite MapSite(Site site, Guid scanId) => new()
    {
        ScanId = scanId,
        SiteUrl = site.WebUrl ?? string.Empty,
        Title = site.DisplayName ?? string.Empty,
        // Graph /sites search does not return the site template; derive from URL patterns
        Template = DeriveTemplate(site),
        // Storage used/quota requires the drive resource or SPO admin API — Graph basic
        // site listing does not include quota. Leave as 0; enrich with SPO REST if needed.
        StorageUsedGb = 0,
        StorageQuotaGb = 0,
        Owners = [],            // requires /sites/{id}/permissions — expensive per-site call
        LastActivityDate = null, // requires SharePoint activity reports API
        HasUniquePermissions = false, // requires SPO REST or /permissions enumeration
        SubsiteCount = 0,           // requires /sites/{id}/sites enumeration
    };

    /// <summary>
    /// Derives a template label from URL patterns since the Graph sites endpoint
    /// does not return a template property in the basic select.
    /// </summary>
    private static string DeriveTemplate(Site site)
    {
        var url = site.WebUrl ?? string.Empty;

        if (url.Contains("/sites/", StringComparison.OrdinalIgnoreCase))
            return "TeamSite"; // default for /sites/ path

        if (url.Contains("/portals/", StringComparison.OrdinalIgnoreCase))
            return "CommunicationSite";

        return "SiteCollection";
    }
}
