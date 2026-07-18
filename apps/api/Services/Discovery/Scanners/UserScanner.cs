using Microsoft.Graph;
using Microsoft.Graph.Models;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Discovery.Scanners;

/// <summary>
/// Scans users from the source tenant via Microsoft Graph.
/// </summary>
public class UserScanner
{
    private readonly ILogger<UserScanner> _logger;
    private readonly ITenantRepository _tenantRepo;
    private readonly IGraphClientFactory _graphClientFactory;
    private readonly IKeyVaultCredentialService _keyVault;

    public UserScanner(
        ILogger<UserScanner> logger,
        ITenantRepository tenantRepo,
        IGraphClientFactory graphClientFactory,
        IKeyVaultCredentialService keyVault)
    {
        _logger = logger;
        _tenantRepo = tenantRepo;
        _graphClientFactory = graphClientFactory;
        _keyVault = keyVault;
    }

    public async Task<List<ScannedUser>> ScanAsync(Guid tenantId, Guid scanId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scanning users via Microsoft Graph for tenant {TenantId}", tenantId);

        var tenant = await _tenantRepo.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found — cannot build Graph client.");

        // Load credentials from Key Vault (all nulls when KV is disabled — falls back to tenant model)
        var (certBase64, certPassword, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, cancellationToken);
        var graphClient = _graphClientFactory.CreateForTenant(tenant, certBase64, certPassword, secret);

        var results = new List<ScannedUser>();

        try
        {
            var response = await graphClient.Users.GetAsync(req =>
            {
                req.QueryParameters.Select =
                [
                    "id", "displayName", "userPrincipalName", "mail",
                    "jobTitle", "department", "officeLocation",
                    "accountEnabled", "assignedLicenses", "createdDateTime",
                    "proxyAddresses", "onPremisesSyncEnabled"
                ];
                req.QueryParameters.Top = 999;
            }, cancellationToken);

            var pageIterator = PageIterator<User, UserCollectionResponse>.CreatePageIterator(
                graphClient,
                response!,
                user =>
                {
                    results.Add(MapUser(user, scanId));
                    return true; // continue iteration
                });

            await pageIterator.IterateAsync(cancellationToken);
        }
        catch (ServiceException ex)
        {
            _logger.LogError(ex,
                "Microsoft Graph returned an error while scanning users for tenant {TenantId}: {Message}",
                tenantId, ex.Message);
            throw;
        }

        _logger.LogInformation(
            "User scan complete for tenant {TenantId}: {Count} users found",
            tenantId, results.Count);

        return results;
    }

    private static ScannedUser MapUser(User user, Guid scanId) => new()
    {
        ScanId = scanId,
        SourceObjectId = user.Id ?? string.Empty,
        DisplayName = user.DisplayName ?? string.Empty,
        DirectorySynced = user.OnPremisesSyncEnabled == true,
        Upn = user.UserPrincipalName ?? string.Empty,
        AccountEnabled = user.AccountEnabled ?? false,
        // Licenses: map SKU GUIDs to strings; real display names require a SKU catalog lookup
        Licenses = user.AssignedLicenses?
            .Where(l => l.SkuId.HasValue)
            .Select(l => l.SkuId!.Value.ToString())
            .ToList() ?? [],
        // Graph does not expose mailbox size or MFA status — leave defaults
        HasMailbox = !string.IsNullOrWhiteSpace(user.Mail),
        MailboxSizeGb = 0,
        MailboxType = "UserMailbox",
        OneDriveSizeGb = 0,
        MfaEnabled = false, // Requires a separate Graph call to authenticationMethods
        // Keep only SMTP entries; Graph also exposes SIP:/X500: which we don't want on the target.
        ProxyAddresses = (user.ProxyAddresses ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
            .ToList(),
    };
}
