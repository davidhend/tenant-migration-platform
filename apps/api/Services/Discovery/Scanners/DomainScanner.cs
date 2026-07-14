using Microsoft.Graph;
using Microsoft.Graph.Models;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Discovery.Scanners;

/// <summary>
/// Scans verified domains registered to the source tenant via Microsoft Graph.
/// </summary>
public class DomainScanner
{
    private readonly ILogger<DomainScanner> _logger;
    private readonly ITenantRepository _tenantRepo;
    private readonly IGraphClientFactory _graphClientFactory;
    private readonly IKeyVaultCredentialService _keyVault;

    public DomainScanner(
        ILogger<DomainScanner> logger,
        ITenantRepository tenantRepo,
        IGraphClientFactory graphClientFactory,
        IKeyVaultCredentialService keyVault)
    {
        _logger = logger;
        _tenantRepo = tenantRepo;
        _graphClientFactory = graphClientFactory;
        _keyVault = keyVault;
    }

    public async Task<List<ScannedDomain>> ScanAsync(
        Guid tenantId, Guid scanId,
        List<ScannedUser> users,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Scanning domains via Microsoft Graph for tenant {TenantId}", tenantId);

        var tenant = await _tenantRepo.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found — cannot build Graph client.");

        // Load credentials from Key Vault (all nulls when KV is disabled — falls back to tenant model)
        var (certBase64, certPassword, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, cancellationToken);
        var graphClient = _graphClientFactory.CreateForTenant(tenant, certBase64, certPassword, secret);

        var domainList = new List<Domain>();

        try
        {
            var response = await graphClient.Domains.GetAsync(req =>
            {
                req.QueryParameters.Select =
                [
                    "id", "isVerified", "isDefault", "authenticationType"
                ];
            }, cancellationToken);

            // Domain list is typically small (< 50 per tenant) — iterate pages defensively
            var pageIterator = PageIterator<Domain, DomainCollectionResponse>.CreatePageIterator(
                graphClient,
                response!,
                domain =>
                {
                    domainList.Add(domain);
                    return true;
                });

            await pageIterator.IterateAsync(cancellationToken);
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex,
                "Microsoft Graph returned an error while scanning domains for tenant {TenantId}: {Message}",
                tenantId, ex.Message);
            throw;
        }

        // Build a lookup of domain → user count from the already-scanned users
        var userCountByDomain = BuildDomainUserCounts(users);

        var results = domainList.Select(domain => new ScannedDomain
        {
            ScanId = scanId,
            Name = domain.Id ?? string.Empty,
            IsVerified = domain.IsVerified ?? false,
            IsDefault = domain.IsDefault ?? false,
            // Count users whose UPN suffix matches this domain
            UserCount = userCountByDomain.TryGetValue(domain.Id ?? string.Empty, out var count)
                ? count
                : 0,
        }).ToList();

        _logger.LogInformation(
            "Domain scan complete for tenant {TenantId}: {Count} domains found",
            tenantId, results.Count);

        return results;
    }

    /// <summary>
    /// Builds a dictionary of domain name → user count by extracting the UPN
    /// suffix from each scanned user.
    /// </summary>
    private static Dictionary<string, int> BuildDomainUserCounts(List<ScannedUser> users)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var atIdx = user.Upn.IndexOf('@', StringComparison.Ordinal);
            if (atIdx < 0) continue;

            var domain = user.Upn[(atIdx + 1)..];
            counts[domain] = counts.TryGetValue(domain, out var existing) ? existing + 1 : 1;
        }

        return counts;
    }
}
