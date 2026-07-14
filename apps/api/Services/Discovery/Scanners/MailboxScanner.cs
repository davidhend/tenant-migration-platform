using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Discovery.Scanners;

/// <summary>
/// Scans Exchange Online mailboxes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Production path:</b> Microsoft Graph does not expose Exchange-specific mailbox
/// details (size, item count, archive status, last logon) directly.  The Graph
/// <c>/users</c> endpoint is used to enumerate mail-enabled accounts; size/item
/// data is fetched from the EXO REST API via <see cref="IExoRestClient"/> when
/// tenant credentials are available.  Fields that cannot be populated are returned
/// as zero/false with a documented caveat.
/// </para>
/// </remarks>
public class MailboxScanner
{
    private readonly ILogger<MailboxScanner> _logger;
    private readonly ITenantRepository _tenantRepo;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly ITenantCredentialFactory _credentialFactory;
    private readonly IExoRestClient _exoClient;

    public MailboxScanner(
        ILogger<MailboxScanner> logger,
        ITenantRepository tenantRepo,
        IKeyVaultCredentialService keyVault,
        ITenantCredentialFactory credentialFactory,
        IExoRestClient exoClient)
    {
        _logger = logger;
        _tenantRepo = tenantRepo;
        _keyVault = keyVault;
        _credentialFactory = credentialFactory;
        _exoClient = exoClient;
    }

    /// <summary>
    /// Derives <see cref="ScannedMailbox"/> records from the user list returned by
    /// <see cref="UserScanner"/>. When tenant credentials are available, enriches
    /// each mailbox with EXO statistics (size, item count, archive info, last logon).
    /// </summary>
    public async Task<List<ScannedMailbox>> ScanAsync(
        Guid tenantId,
        Guid scanId,
        List<ScannedUser> users,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Deriving mailboxes from {UserCount} Graph users for tenant {TenantId}.",
            users.Count, tenantId);

        // Filter to mail-enabled users identified by the UserScanner
        var mailboxes = users
            .Where(u => u.HasMailbox)
            .Select(u => new ScannedMailbox
            {
                ScanId = scanId,
                DisplayName = u.DisplayName,
                PrimarySmtpAddress = u.Upn,
                // Graph does not reliably distinguish shared vs user mailboxes
                // without an EXO call; default to UserMailbox
                MailboxType = "UserMailbox",
                SizeGb = 0,
                ItemCount = 0,
                LastLogonTime = null,
                HasArchive = false,
                ArchiveSizeGb = null,
            })
            .ToList();

        _logger.LogInformation(
            "Mailbox scan: {Count} mail-enabled accounts found for tenant {TenantId}. Attempting EXO enrichment.",
            mailboxes.Count, tenantId);

        // Attempt to enrich with EXO statistics if credentials are available
        await TryEnrichWithExoStatsAsync(tenantId, mailboxes, cancellationToken);

        _logger.LogInformation(
            "Mailbox scan complete for tenant {TenantId}: {Count} mailboxes.",
            tenantId, mailboxes.Count);

        return mailboxes;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task TryEnrichWithExoStatsAsync(
        Guid tenantId,
        List<ScannedMailbox> mailboxes,
        CancellationToken ct)
    {
        // Load the tenant entity to check credentials
        var tenant = await _tenantRepo.GetByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            _logger.LogWarning(
                "MailboxScanner: tenant {TenantId} not found in repository — skipping EXO enrichment.",
                tenantId);
            return;
        }

        // Load credentials from Key Vault (returns all-null when KV is disabled)
        var (kvCertBase64, kvCertPassword, kvSecret) = await _keyVault.LoadCredentialsAsync(tenantId, ct);

        // Determine if any credentials are available
        var hasCert = !string.IsNullOrWhiteSpace(kvCertBase64 ?? tenant.ClientCertificateBase64);
        var hasSecret = !string.IsNullOrWhiteSpace(kvSecret ?? tenant.ClientSecretPlain);

        if (!hasCert && !hasSecret)
        {
            _logger.LogInformation(
                "MailboxScanner: no credentials available for tenant {TenantId} — skipping EXO enrichment. " +
                "Size, ItemCount, and archive fields will be 0/false.",
                tenantId);
            return;
        }

        Azure.Core.TokenCredential credential;
        try
        {
            credential = _credentialFactory.CreateCredential(tenant, kvCertBase64, kvCertPassword, kvSecret);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex,
                "MailboxScanner: failed to build credential for tenant {TenantId} — skipping EXO enrichment.",
                tenantId);
            return;
        }

        // Probe the first mailbox to check for auth/permission errors before processing the full list
        if (mailboxes.Count > 0)
        {
            try
            {
                await _exoClient.GetMailboxStatisticsAsync(tenant.TenantId, mailboxes[0].PrimarySmtpAddress, credential, ct);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("401") || ex.Message.Contains("403") ||
                ex.Message.Contains("Unauthorized") || ex.Message.Contains("Forbidden") ||
                ex.Message.Contains("Access") || ex.Message.Contains("permission"))
            {
                _logger.LogWarning(
                    "MailboxScanner: EXO returned an auth/permission error for tenant {TenantId}. " +
                    "Ensure the app registration has the Exchange.ManageAsApp application permission " +
                    "and Exchange Online admin consent has been granted. Skipping EXO enrichment. Error: {Error}",
                    tenantId, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MailboxScanner: probe call to EXO for tenant {TenantId} failed — skipping full EXO enrichment.",
                    tenantId);
                return;
            }
        }

        // Enrich all mailboxes concurrently with a concurrency limit of 10
        var semaphore = new SemaphoreSlim(10);
        var tasks = mailboxes.Select(async mailbox =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await EnrichMailboxAsync(tenant.TenantId, mailbox, credential, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task EnrichMailboxAsync(
        string aadTenantId,
        ScannedMailbox mailbox,
        Azure.Core.TokenCredential credential,
        CancellationToken ct)
    {
        try
        {
            var stats = await _exoClient.GetMailboxStatisticsAsync(aadTenantId, mailbox.PrimarySmtpAddress, credential, ct);
            if (stats is not null)
            {
                mailbox.SizeGb = Math.Round(stats.TotalItemSizeBytes / (1024.0 * 1024.0 * 1024.0), 3);
                mailbox.ItemCount = (int)Math.Min(stats.ItemCount, int.MaxValue);
                mailbox.LastLogonTime = stats.LastLogonTime;
            }

            var archive = await _exoClient.GetMailboxArchiveInfoAsync(aadTenantId, mailbox.PrimarySmtpAddress, credential, ct);
            mailbox.HasArchive = archive.HasArchive;
            mailbox.ArchiveSizeGb = archive.HasArchive
                ? Math.Round(archive.ArchiveSizeBytes / (1024.0 * 1024.0 * 1024.0), 3)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MailboxScanner: failed to retrieve EXO stats for mailbox {Upn} — leaving fields at 0/false.",
                mailbox.PrimarySmtpAddress);
            // Do not rethrow — other mailboxes should still be enriched
        }
    }
}
