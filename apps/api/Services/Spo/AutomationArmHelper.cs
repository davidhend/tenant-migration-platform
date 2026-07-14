using Azure.Core;
using Azure.Identity;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Spo;

/// <summary>
/// Azure Automation account settings resolved from configuration.
/// </summary>
public sealed record AutomationSettings(
    string SubscriptionId,
    string ResourceGroup,
    string AccountName,
    string RunbookName,
    TimeSpan PollInterval,
    TimeSpan Timeout)
{
    /// <summary>True when the three account-identifying fields are all present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId) &&
        !string.IsNullOrWhiteSpace(ResourceGroup) &&
        !string.IsNullOrWhiteSpace(AccountName);

    /// <summary>ARM resource URL of the Automation account (no trailing slash, no api-version).</summary>
    public string AccountBaseUrl =>
        $"https://management.azure.com/subscriptions/{SubscriptionId}" +
        $"/resourceGroups/{ResourceGroup}" +
        $"/providers/Microsoft.Automation/automationAccounts/{AccountName}";
}

/// <summary>
/// Shared plumbing for talking to the Azure Automation ARM surface: resolves the
/// <c>Azure:Automation</c> settings fresh per call (so SettingsController edits
/// apply without a restart) and builds the ARM <see cref="TokenCredential"/> from
/// <c>Azure:Identity</c> with a <see cref="DefaultAzureCredential"/> fallback.
/// Used by <see cref="SpoRestClient"/> (job submission) and
/// <see cref="RunbookAutoPublisher"/> (runbook content sync).
/// </summary>
public sealed class AutomationArmHelper
{
    public const string ArmScope   = "https://management.azure.com/.default";
    public const string ApiVersion = "2019-06-01";

    private readonly IConfiguration _configuration;
    private readonly IPlatformSecretResolver _secrets;
    private readonly ILogger<AutomationArmHelper> _logger;

    public AutomationArmHelper(
        IConfiguration configuration,
        IPlatformSecretResolver secrets,
        ILogger<AutomationArmHelper> logger)
    {
        _configuration = configuration;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Read the Automation account settings fresh from configuration.</summary>
    public AutomationSettings LoadSettings()
    {
        var section = _configuration.GetSection("Azure:Automation");
        return new AutomationSettings(
            SubscriptionId: section["SubscriptionId"] ?? "",
            ResourceGroup:  section["ResourceGroup"]  ?? "",
            AccountName:    section["AccountName"]    ?? "",
            RunbookName:    section["RunbookName"]    ?? "Invoke-SpoCrossTenantOperation",
            PollInterval:   TimeSpan.FromSeconds(section.GetValue("JobPollIntervalSeconds", 10)),
            Timeout:        TimeSpan.FromMinutes(section.GetValue("JobTimeoutMinutes", 15)));
    }

    /// <summary>
    /// Build the ARM credential fresh each call. If Azure:Identity is configured
    /// (via the Settings UI), use the configured auth method with secret material
    /// resolved through the platform secret store (Key Vault when enabled, the
    /// override file otherwise); fall back to DefaultAzureCredential (env vars,
    /// managed identity, az login, etc.).
    /// </summary>
    public async Task<TokenCredential> BuildCredentialAsync(CancellationToken ct)
    {
        var identity   = _configuration.GetSection("Azure:Identity");
        var tenantId   = identity["TenantId"];
        var clientId   = identity["ClientId"];
        var authMethod = (identity["AuthMethod"] ?? "secret").ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogDebug("Azure:Identity not configured — falling back to DefaultAzureCredential.");
            return new DefaultAzureCredential();
        }

        if (authMethod == "certificate")
        {
            var certB64 = await _secrets.GetAsync("Azure:Identity:CertificateBase64", ct);
            var certPwd = await _secrets.GetAsync("Azure:Identity:CertificatePassword", ct);
            if (string.IsNullOrWhiteSpace(certB64))
            {
                _logger.LogWarning("Azure:Identity AuthMethod=certificate but no certificate is stored — falling back to DefaultAzureCredential.");
                return new DefaultAzureCredential();
            }

            _logger.LogDebug("Using configured service principal (certificate) for ARM authentication.");
            var pfxBytes = Convert.FromBase64String(certB64);
            var cert = string.IsNullOrEmpty(certPwd)
                ? new System.Security.Cryptography.X509Certificates.X509Certificate2(pfxBytes)
                : new System.Security.Cryptography.X509Certificates.X509Certificate2(pfxBytes, certPwd);
            return new ClientCertificateCredential(tenantId, clientId, cert);
        }

        var clientSecret = await _secrets.GetAsync("Azure:Identity:ClientSecret", ct);
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            _logger.LogDebug("Using configured service principal (secret) for ARM authentication.");
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        _logger.LogDebug("Azure:Identity configured but missing credentials — falling back to DefaultAzureCredential.");
        return new DefaultAzureCredential();
    }

    /// <summary>Acquire an ARM bearer token.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var credential = await BuildCredentialAsync(ct);
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { ArmScope }), ct);
        return token.Token;
    }
}
