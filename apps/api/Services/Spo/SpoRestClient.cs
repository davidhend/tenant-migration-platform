using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MigrationPlatform.Api.Services.Spo;

/// <summary>
/// <see cref="ISpoRestClient"/> implementation that drives the
/// <c>Microsoft.Online.SharePoint.PowerShell</c> module indirectly by triggering
/// an Azure Automation runbook on a Microsoft-managed Windows sandbox. The Linux
/// API container cannot load the SPO module (Windows-only), so the published
/// <c>Invoke-SpoCrossTenantOperation</c> runbook is the execution host.
///
/// <para>
/// Each call PUTs a new job to the Azure Automation REST API with the operation
/// name and parameters (including the tenant's app-only certificate as base64),
/// polls <c>/jobs/{jobId}</c> until the job reaches a terminal status, then GETs
/// <c>/jobs/{jobId}/output</c> and parses the single-line JSON the runbook emits.
/// </para>
///
/// <para>Required config (<c>Azure:Automation</c>): SubscriptionId, ResourceGroup,
/// AccountName, RunbookName. The API's identity (DefaultAzureCredential) must
/// hold the Automation Job Operator role on the Automation account.</para>
/// </summary>
public sealed class SpoRestClient : ISpoRestClient
{
    private const string ApiVersion = AutomationArmHelper.ApiVersion;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SpoRestClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly AutomationArmHelper _arm;

    public SpoRestClient(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        AutomationArmHelper arm,
        ILogger<SpoRestClient> logger)
    {
        // Do NOT cache an HttpClient here: this service is a singleton, and a
        // client captured at construction would pin its handler forever,
        // defeating IHttpClientFactory handler rotation (stale DNS/connections
        // over a long-lived process). Create per call instead.
        _httpFactory   = httpFactory;
        _logger        = logger;
        _configuration = configuration;
        _arm           = arm;
    }

    // ── ISpoRestClient ────────────────────────────────────────────────────────

    public async Task<SpoMigrationJobResult> StartUserContentMoveAsync(
        string sourceAdminUrl,
        string sourceUpn,
        string targetUpn,
        string targetCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("Start", sourceAdminUrl, credentials);
        parameters["SourceUpn"]                = sourceUpn;
        parameters["TargetUpn"]                = targetUpn;
        parameters["TargetCrossTenantHostUrl"] = targetCrossTenantHostUrl;

        var json = await RunRunbookAsync(parameters, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new SpoMigrationJobResult(
            JobId:  root.GetProperty("JobId").GetString() ?? sourceUpn,
            Status: root.GetProperty("Status").GetString() ?? "Scheduled");
    }

    public async Task<SpoMigrationJobStatus?> GetUserContentMoveStateAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        string sourceUpn,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("GetState", sourceAdminUrl, credentials);
        parameters["SourceUpn"]                 = sourceUpn;
        parameters["PartnerCrossTenantHostUrl"] = partnerCrossTenantHostUrl;

        var json = await RunRunbookAsync(parameters, ct);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.GetProperty("Status").GetString() ?? "Unknown";
        var progress = root.TryGetProperty("ProgressPercent", out var pp) ? pp.GetInt32() : DeriveProgress(status);
        var error = root.TryGetProperty("ErrorMessage", out var em) && em.ValueKind == JsonValueKind.String
            ? em.GetString() : null;

        return new SpoMigrationJobStatus(sourceUpn, status, progress, error);
    }

    public async Task<SpoMigrationJobResult> StartSiteContentMoveAsync(
        string sourceAdminUrl,
        string sourceSiteUrl,
        string targetSiteUrl,
        string targetCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("StartSite", sourceAdminUrl, credentials);
        parameters["SourceSiteUrl"]             = sourceSiteUrl;
        parameters["TargetSiteUrl"]             = targetSiteUrl;
        parameters["TargetCrossTenantHostUrl"]  = targetCrossTenantHostUrl;

        var json = await RunRunbookAsync(parameters, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new SpoMigrationJobResult(
            JobId:  root.GetProperty("JobId").GetString() ?? sourceSiteUrl,
            Status: root.GetProperty("Status").GetString() ?? "Scheduled");
    }

    public async Task<SpoMigrationJobStatus?> GetSiteContentMoveStateAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        string sourceSiteUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("GetSiteState", sourceAdminUrl, credentials);
        parameters["SourceSiteUrl"]              = sourceSiteUrl;
        parameters["PartnerCrossTenantHostUrl"]  = partnerCrossTenantHostUrl;

        var json = await RunRunbookAsync(parameters, ct);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.GetProperty("Status").GetString() ?? "Unknown";
        var progress = root.TryGetProperty("ProgressPercent", out var pp) ? pp.GetInt32() : DeriveProgress(status);
        var error = root.TryGetProperty("ErrorMessage", out var em) && em.ValueKind == JsonValueKind.String
            ? em.GetString() : null;

        return new SpoMigrationJobStatus(sourceSiteUrl, status, progress, error);
    }

    public async Task<SpoCompatibilityResult> CheckCrossTenantCompatibilityAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("Compatibility", sourceAdminUrl, credentials);
        parameters["PartnerCrossTenantHostUrl"] = partnerCrossTenantHostUrl;

        try
        {
            var json = await RunRunbookAsync(parameters, ct);
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("Status").GetString() ?? string.Empty;
            _logger.LogDebug("SPO compatibility status for {Partner}: {Status}", partnerCrossTenantHostUrl, status);

            // Older runbook versions may return the full PowerShell stringification
            // of the object (e.g. "@{...CompatibilityStatus=Compatible}") — extract
            // the value, then match EXACTLY. A Contains() check here is dangerous:
            // "Incompatible" contains "Compatible" and would pass preflight.
            return new SpoCompatibilityResult(IsCompatibleStatus(status), status, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SPO compatibility probe failed for {Partner}.", partnerCrossTenantHostUrl);
            return new SpoCompatibilityResult(false, null, ex.Message);
        }
    }

    public async Task<string?> SetCrossTenantRelationshipAsync(
        string adminUrl,
        string partnerRole,
        string partnerCrossTenantHostUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("SetCrossTenantRelationship", adminUrl, credentials);
        parameters["PartnerRole"]               = partnerRole;
        parameters["PartnerCrossTenantHostUrl"] = partnerCrossTenantHostUrl;

        var json = await RunRunbookAsync(parameters, ct);
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        _logger.LogInformation(
            "Set-SPOCrossTenantRelationship on {AdminUrl} (PartnerRole={Role}, Partner={Partner}) — test status: {Status}.",
            adminUrl, partnerRole, partnerCrossTenantHostUrl, status ?? "unknown");
        return status;
    }

    public async Task<SpoRelationshipResult> EnsureCrossTenantRelationshipAsync(
        string sourceAdminUrl,
        string targetAdminUrl,
        string sourceCrossTenantHostUrl,
        string targetCrossTenantHostUrl,
        SpoPowerShellCredentials sourceCredentials,
        SpoPowerShellCredentials targetCredentials,
        bool skipPrecheck,
        CancellationToken ct)
    {
        if (!skipPrecheck)
        {
            var pre = await CheckCrossTenantCompatibilityAsync(
                sourceAdminUrl, targetCrossTenantHostUrl, sourceCredentials, ct);
            if (pre.IsCompatible)
                return new SpoRelationshipResult(true, pre.Status, null, null, null);
        }

        // Refine the derived "-my" host URLs with each tenant's canonical value
        // when resolvable — the verified sequence passes exactly what
        // Get-SPOCrossTenantHostUrl returns on the partner tenant.
        sourceCrossTenantHostUrl = await ResolveHostUrlOrFallbackAsync(
            sourceAdminUrl, sourceCrossTenantHostUrl, sourceCredentials, ct);
        targetCrossTenantHostUrl = await ResolveHostUrlOrFallbackAsync(
            targetAdminUrl, targetCrossTenantHostUrl, targetCredentials, ct);

        string? targetSide = null;
        string? sourceSide = null;
        try
        {
            // Destination first, then source — the verified working order. Each
            // side names the PARTNER's role: target passes Source, source passes Target.
            targetSide = await SetCrossTenantRelationshipAsync(
                targetAdminUrl, "Source", sourceCrossTenantHostUrl, targetCredentials, ct);
            sourceSide = await SetCrossTenantRelationshipAsync(
                sourceAdminUrl, "Target", targetCrossTenantHostUrl, sourceCredentials, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Automatic SPO cross-tenant relationship establishment failed (target-side test: {Target}, source-side test: {Source}).",
                targetSide ?? "not run", sourceSide ?? "not run");
            return new SpoRelationshipResult(false, null, targetSide, sourceSide, ex.Message);
        }

        var post = await CheckCrossTenantCompatibilityAsync(
            sourceAdminUrl, targetCrossTenantHostUrl, sourceCredentials, ct);
        return new SpoRelationshipResult(
            post.IsCompatible,
            post.Status,
            targetSide,
            sourceSide,
            post.IsCompatible
                ? null
                : post.ErrorMessage ?? $"Compatibility status after establishing the relationship: '{post.Status}'.");
    }

    private async Task<string> ResolveHostUrlOrFallbackAsync(
        string adminUrl, string fallback, SpoPowerShellCredentials credentials, CancellationToken ct)
    {
        try
        {
            var resolved = await GetCrossTenantHostUrlAsync(adminUrl, credentials, ct);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                if (!resolved.Equals(fallback, StringComparison.OrdinalIgnoreCase))
                    _logger.LogWarning(
                        "Canonical cross-tenant host URL for {AdminUrl} is {Resolved}, not the derived {Fallback} — using the canonical value.",
                        adminUrl, resolved, fallback);
                return resolved;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Could not resolve the canonical cross-tenant host URL for {AdminUrl} — using derived {Fallback}.",
                adminUrl, fallback);
        }
        return fallback;
    }

    /// <summary>
    /// Extracts the bare status token from either a clean status string
    /// ("Compatible") or a legacy PowerShell object stringification
    /// ("@{Foo=Bar; CompatibilityStatus=Compatible}").
    /// </summary>
    /// <summary>
    /// True when the (possibly legacy-stringified) status means the relationship
    /// is usable: exactly "Compatible" or "Warning". Exact match only — a
    /// Contains() check is dangerous because "Incompatible" contains "Compatible".
    /// </summary>
    internal static bool IsCompatibleStatus(string status)
    {
        var normalized = NormalizeCompatibilityStatus(status);
        return normalized.Equals("Compatible", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Warning", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeCompatibilityStatus(string status)
    {
        var idx = status.IndexOf("CompatibilityStatus=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return status.Trim();

        var value = status[(idx + "CompatibilityStatus=".Length)..];
        var end = value.IndexOfAny(new[] { ';', '}', ' ' });
        return (end >= 0 ? value[..end] : value).Trim();
    }

    public async Task<IReadOnlyList<SpoMigrationJobStatus>> GetUserContentMoveStatesAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        IReadOnlyCollection<string> sourceUpns,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
        => await GetContentMoveStatesBatchAsync(
            "GetStateBatch", sourceAdminUrl, partnerCrossTenantHostUrl, sourceUpns, credentials, ct);

    public async Task<IReadOnlyList<SpoMigrationJobStatus>> GetSiteContentMoveStatesAsync(
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        IReadOnlyCollection<string> sourceSiteUrls,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
        => await GetContentMoveStatesBatchAsync(
            "GetSiteStateBatch", sourceAdminUrl, partnerCrossTenantHostUrl, sourceSiteUrls, credentials, ct);

    private async Task<IReadOnlyList<SpoMigrationJobStatus>> GetContentMoveStatesBatchAsync(
        string operation,
        string sourceAdminUrl,
        string partnerCrossTenantHostUrl,
        IReadOnlyCollection<string> identities,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        if (identities.Count == 0)
            return Array.Empty<SpoMigrationJobStatus>();

        var idsJson = JsonSerializer.Serialize(identities);
        var parameters = BaseParameters(operation, sourceAdminUrl, credentials);
        parameters["PartnerCrossTenantHostUrl"] = partnerCrossTenantHostUrl;
        parameters["IdentitiesJsonBase64"]      = Convert.ToBase64String(Encoding.UTF8.GetBytes(idsJson));

        var json = await RunRunbookAsync(parameters, ct);
        var results = new List<SpoMigrationJobStatus>(identities.Count);
        using var doc = JsonDocument.Parse(json);
        // PS 5.1 ConvertTo-Json can unwrap a single-element result set to a bare
        // object despite the runbook's array-forcing (observed live 2026-07-09) —
        // accept an object root as a one-element batch.
        var elements = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array  => doc.RootElement.EnumerateArray().ToList(),
            JsonValueKind.Object => new List<JsonElement> { doc.RootElement },
            _ => throw new InvalidOperationException(
                $"Runbook {operation} returned unexpected JSON (expected array): {json}"),
        };

        foreach (var el in elements)
        {
            var jobId  = el.TryGetProperty("JobId", out var ji) ? ji.GetString() ?? "" : "";
            var status = el.TryGetProperty("Status", out var st) ? st.GetString() ?? "Unknown" : "Unknown";
            var progress = el.TryGetProperty("ProgressPercent", out var pp) && pp.ValueKind == JsonValueKind.Number
                ? pp.GetInt32() : DeriveProgress(status);
            var error = el.TryGetProperty("ErrorMessage", out var em) && em.ValueKind == JsonValueKind.String
                ? em.GetString() : null;
            results.Add(new SpoMigrationJobStatus(jobId, status, progress, error));
        }

        return results;
    }

    public async Task<string?> GetCrossTenantHostUrlAsync(
        string adminUrl,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("GetCrossTenantHostUrl", adminUrl, credentials);
        var json = await RunRunbookAsync(parameters, ct);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
            return null;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("Url", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString()
            : null;
    }

    public async Task UploadIdentityMapAsync(
        string targetAdminUrl,
        string identityMapCsvBase64,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var parameters = BaseParameters("UploadIdentityMap", targetAdminUrl, credentials);
        parameters["IdentityMapCsvBase64"] = identityMapCsvBase64;

        var json = await RunRunbookAsync(parameters, ct);
        _logger.LogInformation("Identity map upload result: {Result}", json);
    }

    public async Task RequestPersonalSiteAsync(
        string targetAdminUrl,
        IEnumerable<string> upns,
        SpoPowerShellCredentials credentials,
        CancellationToken ct)
    {
        var upnArray = upns
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (upnArray.Length == 0)
        {
            _logger.LogDebug("RequestPersonalSiteAsync called with no UPNs — skipping.");
            return;
        }

        var upnsJson = JsonSerializer.Serialize(upnArray);
        var upnsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(upnsJson));

        var parameters = BaseParameters("RequestPersonalSite", targetAdminUrl, credentials);
        parameters["TargetUpnsJsonBase64"] = upnsBase64;

        var json = await RunRunbookAsync(parameters, ct);
        _logger.LogInformation(
            "Requested {Count} personal site(s) via SPO runbook; result: {Result}",
            upnArray.Length, json);
    }

    // ── Azure Automation REST orchestration ─────────────────────────────────

    private Dictionary<string, string> BaseParameters(
        string operation, string sourceAdminUrl, SpoPowerShellCredentials credentials)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Operation"]      = operation,
            ["TenantId"]       = credentials.TenantId,
            ["ClientId"]       = credentials.ClientId,
            ["SourceAdminUrl"] = sourceAdminUrl,
        };

        // Preferred: let the runbook pull the PFX from Key Vault with the
        // Automation account's managed identity, so the certificate and its
        // password never appear as portal-visible job parameters.
        var useKeyVault = _configuration.GetValue("Azure:Automation:UseKeyVaultCertificate", false);
        var keyVaultUrl = _configuration["Azure:Automation:KeyVaultUrl"];
        if (string.IsNullOrWhiteSpace(keyVaultUrl))
            keyVaultUrl = _configuration["KeyVault:VaultUri"];

        if (useKeyVault &&
            !string.IsNullOrWhiteSpace(keyVaultUrl) &&
            !string.IsNullOrWhiteSpace(credentials.KeyVaultCertificateName))
        {
            parameters["KeyVaultUrl"]             = keyVaultUrl!;
            parameters["KeyVaultCertificateName"] = credentials.KeyVaultCertificateName!;
        }
        else
        {
            if (useKeyVault)
            {
                _logger.LogWarning(
                    "Azure:Automation:UseKeyVaultCertificate is true but KeyVaultUrl or the credential's " +
                    "KeyVaultCertificateName is missing — falling back to inline PFX job parameters.");
            }
            parameters["CertificatePfxBase64"] = credentials.CertificatePfxBase64;
            parameters["CertificatePassword"]  = credentials.CertificatePassword ?? string.Empty;
        }

        return parameters;
    }

    private async Task<string> RunRunbookAsync(Dictionary<string, string> parameters, CancellationToken ct)
    {
        var config = _arm.LoadSettings();
        if (!config.IsConfigured)
        {
            throw new InvalidOperationException(
                "Azure:Automation is not configured. Set SubscriptionId, ResourceGroup, AccountName via the Settings page or appsettings.");
        }

        var http = _httpFactory.CreateClient("spo");
        var bearer = await _arm.GetTokenAsync(ct);
        var jobId = Guid.NewGuid();
        var baseUrl = $"{config.AccountBaseUrl}/jobs/{jobId}?api-version={ApiVersion}";

        // ── PUT job ──────────────────────────────────────────────────────────
        var body = new
        {
            properties = new
            {
                runbook    = new { name = config.RunbookName },
                parameters = parameters,
            }
        };

        using var putReq = new HttpRequestMessage(HttpMethod.Put, baseUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var putResp = await http.SendAsync(putReq, ct);
        if (!putResp.IsSuccessStatusCode)
        {
            var err = await putResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Failed to submit Automation runbook job ({(int)putResp.StatusCode}): {err}");
        }

        _logger.LogInformation(
            "Submitted SPO Automation job {JobId} (operation={Operation}).",
            jobId, parameters.GetValueOrDefault("Operation"));

        // ── Poll until terminal ──────────────────────────────────────────────
        var deadline = DateTime.UtcNow + config.Timeout;
        string? terminalStatus = null;
        string? jobException = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(config.PollInterval, ct);

            using var getReq = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var getResp = await http.SendAsync(getReq, ct);
            if (!getResp.IsSuccessStatusCode)
            {
                var err = await getResp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Failed to poll Automation job {jobId} ({(int)getResp.StatusCode}): {err}");
            }

            var payload = await getResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("properties", out var props) &&
                props.TryGetProperty("status", out var st))
            {
                var status = st.GetString() ?? "";
                if (props.TryGetProperty("exception", out var ex) && ex.ValueKind == JsonValueKind.String)
                    jobException = ex.GetString();
                if (IsTerminal(status))
                {
                    terminalStatus = status;
                    break;
                }
            }
        }

        if (terminalStatus is null)
        {
            throw new InvalidOperationException(
                $"Azure Automation job {jobId} did not complete within {config.Timeout.TotalMinutes:0} minutes.");
        }

        // ── Fetch output stream ──────────────────────────────────────────────
        var outputUrl = $"{config.AccountBaseUrl}/jobs/{jobId}/output?api-version={ApiVersion}";

        using var outReq = new HttpRequestMessage(HttpMethod.Get, outputUrl);
        outReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var outResp = await http.SendAsync(outReq, ct);
        var outputText = await outResp.Content.ReadAsStringAsync(ct);

        if (!terminalStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            var errorDetail = await FetchJobErrorStreamAsync(config, jobId, bearer, ct);
            var combinedError = string.Join(" | ", new[] { jobException, errorDetail, outputText }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            _logger.LogWarning(
                "SPO Automation job {JobId} ended with status {Status}. Exception: {Exception}. Output: {Output}. Errors: {Errors}",
                jobId, terminalStatus, jobException, outputText, errorDetail);
            throw new InvalidOperationException(
                $"SPO Automation job {jobId} ended with status '{terminalStatus}'. {(string.IsNullOrWhiteSpace(combinedError) ? "No error details available — check the runbook in the Azure portal." : combinedError)}");
        }

        return ExtractJsonLine(outputText);
    }

    private async Task<string> FetchJobErrorStreamAsync(
        AutomationSettings config, Guid jobId, string bearerToken, CancellationToken ct)
    {
        try
        {
            var streamsUrl =
                $"{config.AccountBaseUrl}/jobs/{jobId}/streams?$filter=properties/streamType eq 'Error'&api-version={ApiVersion}";

            using var req = new HttpRequestMessage(HttpMethod.Get, streamsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            using var resp = await _httpFactory.CreateClient("spo").SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var messages = new List<string>();
            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    if (entry.TryGetProperty("properties", out var props) &&
                        props.TryGetProperty("summary", out var summary))
                    {
                        var text = summary.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            messages.Add(text);
                    }
                }
            }

            return messages.Count > 0 ? string.Join(" | ", messages) : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch error stream for Automation job {JobId}.", jobId);
            return string.Empty;
        }
    }

    private static bool IsTerminal(string status) =>
        status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Failed",    StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Stopped",   StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Suspended", StringComparison.OrdinalIgnoreCase);

    // The /output endpoint returns plain text (all Write-Output lines concatenated).
    // The runbook emits exactly one JSON line (or the literal 'null'). Pick the last
    // non-empty line to be resilient to any preamble from the sandbox.
    private static string ExtractJsonLine(string outputText)
    {
        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line == "null" || line.StartsWith("{") || line.StartsWith("["))
                return line;
        }
        return outputText.Trim();
    }

    private static int DeriveProgress(string status) => status switch
    {
        "NotStarted"     => 0,
        "Scheduled"      => 5,
        "ReadyToTrigger" => 10,
        "InProgress"     => 50,
        "Rescheduled"    => 25,
        "Success"        => 100,
        "Failed"         => 0,
        _                => 0,
    };

}
