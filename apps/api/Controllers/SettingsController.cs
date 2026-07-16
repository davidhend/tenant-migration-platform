using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services.Spo;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Read/write runtime-tunable platform settings: Azure Automation account
/// configuration, ARM identity (service principal) for triggering runbooks,
/// and the cross-tenant Mailbox Migration app.
///
/// Non-secret values are persisted to <c>settings.override.json</c> (a
/// reloadOnChange config layer, so changes apply without a restart). Secret
/// VALUES go through <see cref="IPlatformSecretStore"/>: Key Vault when
/// enabled (the file then only carries <c>kv:</c> markers), or the override
/// file itself in file mode (dev without Key Vault). Reads resolve through
/// <see cref="IPlatformSecretResolver"/> and only ever return masks/booleans.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IPlatformSecretStore _secrets;
    private readonly IPlatformSecretResolver _resolver;
    private readonly AutomationArmHelper _arm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        IConfiguration config,
        IWebHostEnvironment env,
        IPlatformSecretStore secrets,
        IPlatformSecretResolver resolver,
        AutomationArmHelper arm,
        IHttpClientFactory httpFactory,
        ILogger<SettingsController> logger)
    {
        _config = config;
        _env = env;
        _secrets = secrets;
        _resolver = resolver;
        _arm = arm;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private string OverridePath => SettingsOverrideFile.GetPath(_env);

    // ── Azure Automation (no secrets) ─────────────────────────────────────────

    /// <summary>Get the current Azure:Automation settings.</summary>
    [HttpGet("azure-automation")]
    public IActionResult GetAzureAutomation()
    {
        var section = _config.GetSection("Azure:Automation");
        return Ok(new AzureAutomationSettings(
            SubscriptionId:          section["SubscriptionId"] ?? "",
            ResourceGroup:           section["ResourceGroup"]  ?? "",
            AccountName:             section["AccountName"]    ?? "",
            RunbookName:             section["RunbookName"]    ?? "Invoke-SpoCrossTenantOperation",
            JobPollIntervalSeconds:  section.GetValue("JobPollIntervalSeconds", 10),
            JobTimeoutMinutes:       section.GetValue("JobTimeoutMinutes", 15)));
    }

    /// <summary>Update the Azure:Automation settings.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("azure-automation")]
    public async Task<IActionResult> UpdateAzureAutomation(
        [FromBody] AzureAutomationSettings req, CancellationToken ct)
    {
        await SettingsOverrideFile.UpdateAsync(OverridePath, root =>
        {
            var azure = root["Azure"] as JsonObject ?? new JsonObject();
            root["Azure"] = azure;

            azure["Automation"] = new JsonObject
            {
                ["SubscriptionId"]         = req.SubscriptionId ?? "",
                ["ResourceGroup"]          = req.ResourceGroup  ?? "",
                ["AccountName"]            = req.AccountName    ?? "",
                ["RunbookName"]            = string.IsNullOrWhiteSpace(req.RunbookName)
                                              ? "Invoke-SpoCrossTenantOperation"
                                              : req.RunbookName,
                ["JobPollIntervalSeconds"] = req.JobPollIntervalSeconds <= 0 ? 10 : req.JobPollIntervalSeconds,
                ["JobTimeoutMinutes"]      = req.JobTimeoutMinutes      <= 0 ? 15 : req.JobTimeoutMinutes,
            };
        }, ct);

        _logger.LogInformation("Azure:Automation settings updated via API.");
        return Ok(req);
    }

    /// <summary>
    /// Live environment scan for the Azure Automation execution environment
    /// (everything the <c>infra/terraform/platform-azure</c> stack deploys):
    /// account exists, the API identity's effective RBAC on it, runbook present
    /// and published, and the required PowerShell modules imported. Read-only —
    /// only ARM GETs are issued.
    /// </summary>
    [HttpPost("azure-automation/verify")]
    public async Task<IActionResult> VerifyAzureAutomation(CancellationToken ct)
    {
        var checks = new List<DiagCheck>();
        var report = new AutomationEnvironmentReport { GeneratedAt = DateTime.UtcNow, Checks = checks };

        // ── Check 1: settings present ────────────────────────────────────────
        var settings = _arm.LoadSettings();
        if (!settings.IsConfigured)
        {
            checks.Add(DiagCheck.Fail(
                "automation.config",
                "Azure:Automation settings configured",
                "Subscription ID, resource group, or account name is missing.",
                remediation: "Fill in the Azure Automation card (values come from the platform-azure Terraform " +
                             "stack's 'platform_settings' output) and save, then re-run this scan."));
            return Ok(Finalize(report));
        }
        checks.Add(DiagCheck.Pass(
            "automation.config",
            "Azure:Automation settings configured",
            $"Account '{settings.AccountName}' in resource group '{settings.ResourceGroup}' " +
            $"(subscription {settings.SubscriptionId})."));

        // ── Check 2: ARM token mints ─────────────────────────────────────────
        var identitySection = _config.GetSection("Azure:Identity");
        var identityConfigured = !string.IsNullOrWhiteSpace(identitySection["TenantId"]) &&
                                 !string.IsNullOrWhiteSpace(identitySection["ClientId"]);
        var identitySource = identityConfigured
            ? $"configured service principal {identitySection["ClientId"]} ({identitySection["AuthMethod"] ?? "secret"})"
            : "DefaultAzureCredential (managed identity / env vars / az login)";

        string bearer;
        try
        {
            bearer = await _arm.GetTokenAsync(ct);
            checks.Add(DiagCheck.Pass(
                "arm.token",
                "Acquire Azure Resource Manager token",
                $"Token acquired via {identitySource}."));
        }
        catch (Exception ex)
        {
            checks.Add(DiagCheck.Fail(
                "arm.token",
                "Acquire Azure Resource Manager token",
                $"Token acquisition failed via {identitySource}: {ex.Message}",
                evidence: ex.ToString(),
                remediation: identityConfigured
                    ? "Verify the service principal credentials in the Azure Identity card (tenant ID, client ID, " +
                      "secret/certificate validity and expiry)."
                    : "Configure a service principal in the Azure Identity card, or ensure the host has a usable " +
                      "DefaultAzureCredential source (managed identity in Azure, 'az login' locally)."));
            return Ok(Finalize(report));
        }

        var http = _httpFactory.CreateClient("spo");

        // ── Check 3: effective RBAC on the account ───────────────────────────
        // Runs FIRST because it works for any identity that holds ANY role on
        // the scope, and the result disambiguates 403s on the resource reads
        // below: a Job Operator identity runs migrations fine but cannot read
        // account/runbook/module metadata, so those 403s must not be reported
        // as "not deployed".
        var canWriteRunbooks = false;
        var canRunJobs = false;
        var (permStatus, permBody) = await ArmGetAsync(
            http, bearer,
            $"{settings.AccountBaseUrl}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", ct);
        if (permStatus == HttpStatusCode.OK)
        {
            canWriteRunbooks = PermissionsCoverAction(permBody, "Microsoft.Automation/automationAccounts/runbooks/write");
            canRunJobs       = PermissionsCoverAction(permBody, "Microsoft.Automation/automationAccounts/jobs/write");

            if (canWriteRunbooks && canRunJobs)
                checks.Add(DiagCheck.Pass(
                    "automation.rbac",
                    "API identity role on the Automation account",
                    "Identity can run jobs AND write runbooks (Automation Contributor level) — runbook " +
                    "auto-publish at API startup works."));
            else if (canRunJobs)
                checks.Add(DiagCheck.Warn(
                    "automation.rbac",
                    "API identity role on the Automation account",
                    "Identity can run jobs but NOT write runbooks (Job Operator level). Migrations work, but the " +
                    "runbook auto-publisher cannot sync script updates — runbook changes must be re-imported manually.",
                    remediation: "Grant the Automation Contributor role on the Automation account to enable " +
                                 "runbook auto-publish."));
            else
                checks.Add(DiagCheck.Fail(
                    "automation.rbac",
                    "API identity role on the Automation account",
                    "Identity cannot start Automation jobs — OneDrive/SharePoint migrations will fail at job submission.",
                    evidence: Truncate(permBody),
                    remediation: "Grant the Automation Contributor role on the Automation account to the API's " +
                                 "Azure identity (the platform-azure stack's api_principal_object_id tfvar does this)."));
        }
        else if (permStatus == HttpStatusCode.NotFound)
        {
            checks.Add(DiagCheck.Fail(
                "automation.rbac",
                "API identity role on the Automation account",
                $"The Automation account scope was not found — account '{settings.AccountName}' does not exist " +
                $"in resource group '{settings.ResourceGroup}' (or the subscription/resource group is wrong).",
                evidence: Truncate(permBody),
                remediation: "Deploy the platform-azure Terraform stack (or create the account manually) and " +
                             "confirm the subscription/resource group/account name fields match the deployed resource."));
            return Ok(Finalize(report));
        }
        else
        {
            checks.Add(DiagCheck.Fail(
                "automation.rbac",
                "API identity role on the Automation account",
                $"The identity holds no role on the Automation account (ARM {(int)permStatus} reading its own " +
                "effective permissions) — job submission and all further checks will fail.",
                evidence: Truncate(permBody),
                remediation: "Grant the Automation Contributor role on the Automation account to the API's " +
                             "Azure identity (the platform-azure stack's api_principal_object_id tfvar does this)."));
            return Ok(Finalize(report));
        }

        // ── Check 4: Automation account exists ───────────────────────────────
        var (accountStatus, accountBody) = await ArmGetAsync(
            http, bearer, $"{settings.AccountBaseUrl}?api-version={AutomationArmHelper.ApiVersion}", ct);
        if (accountStatus == HttpStatusCode.OK)
        {
            var location = TryGetJsonString(accountBody, "location") ?? "?";
            checks.Add(DiagCheck.Pass(
                "automation.account",
                "Automation account exists",
                $"Account '{settings.AccountName}' found (location: {location})."));
        }
        else if (accountStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && canRunJobs)
        {
            // The permissions call above succeeded on this exact scope, so the
            // account exists — this identity just can't read its metadata.
            checks.Add(DiagCheck.Warn(
                "automation.account",
                "Automation account exists",
                "Account exists (the RBAC probe resolved its scope) but this identity cannot read its metadata — " +
                "runbook and module state below cannot be verified either.",
                remediation: "Grant the Automation Contributor role for full verification."));
        }
        else
        {
            checks.Add(DiagCheck.Fail(
                "automation.account",
                "Automation account exists",
                accountStatus == HttpStatusCode.NotFound
                    ? $"Account '{settings.AccountName}' was not found in resource group '{settings.ResourceGroup}'."
                    : $"ARM returned {(int)accountStatus} reading the account.",
                evidence: Truncate(accountBody),
                remediation: "Deploy the platform-azure Terraform stack (or create the account manually) and confirm " +
                             "the subscription/resource group/account name fields match the deployed resource."));
            return Ok(Finalize(report));
        }

        // ── Check 5: runbook exists + published ──────────────────────────────
        var (rbStatus, rbBody) = await ArmGetAsync(
            http, bearer,
            $"{settings.AccountBaseUrl}/runbooks/{settings.RunbookName}?api-version={AutomationArmHelper.ApiVersion}", ct);
        if (rbStatus == HttpStatusCode.OK)
        {
            var state = TryGetJsonString(rbBody, "properties", "state") ?? "?";
            if (string.Equals(state, "Published", StringComparison.OrdinalIgnoreCase))
                checks.Add(DiagCheck.Pass(
                    "automation.runbook",
                    $"Runbook '{settings.RunbookName}' exists and is published",
                    "Runbook found in state Published."));
            else
                checks.Add(DiagCheck.Warn(
                    "automation.runbook",
                    $"Runbook '{settings.RunbookName}' exists and is published",
                    $"Runbook exists but is in state '{state}' — jobs run the last published version.",
                    remediation: "Publish the runbook in the Azure portal, or restart the API with Automation " +
                                 "Contributor access so the auto-publisher republishes it."));
        }
        else if (rbStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && canRunJobs)
        {
            checks.Add(DiagCheck.Warn(
                "automation.runbook",
                $"Runbook '{settings.RunbookName}' exists and is published",
                "Cannot verify — this identity lacks runbook read access (Job Operator level).",
                remediation: "Grant the Automation Contributor role for full verification, or confirm the runbook " +
                             "manually in the Azure portal."));
        }
        else
        {
            checks.Add(DiagCheck.Fail(
                "automation.runbook",
                $"Runbook '{settings.RunbookName}' exists and is published",
                rbStatus == HttpStatusCode.NotFound
                    ? "Runbook not found in the Automation account."
                    : $"ARM returned {(int)rbStatus} reading the runbook.",
                evidence: Truncate(rbBody),
                remediation: "With Automation Contributor access the API creates and publishes the runbook " +
                             "automatically at startup — restart the API, or import " +
                             "apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1 manually (PowerShell 5.1) and publish."));
        }

        // ── Check 6: required modules ────────────────────────────────────────
        var requiredModules = new List<(string Name, string Why)>
        {
            ("Microsoft.Online.SharePoint.PowerShell", "runs every SPO cross-tenant cmdlet"),
        };
        if (_config.GetValue("Azure:Automation:UseKeyVaultCertificate", false))
        {
            requiredModules.Add(("Az.Accounts", "Key Vault certificate mode (UseKeyVaultCertificate=true)"));
            requiredModules.Add(("Az.KeyVault", "Key Vault certificate mode (UseKeyVaultCertificate=true)"));
        }

        foreach (var (module, why) in requiredModules)
        {
            var checkId = $"automation.module.{module.ToLowerInvariant()}";
            var (modStatus, modBody) = await ArmGetAsync(
                http, bearer,
                $"{settings.AccountBaseUrl}/modules/{module}?api-version={AutomationArmHelper.ApiVersion}", ct);
            if (modStatus == HttpStatusCode.OK)
            {
                var state = TryGetJsonString(modBody, "properties", "provisioningState") ?? "?";
                if (string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase))
                    checks.Add(DiagCheck.Pass(checkId, $"Module '{module}' imported",
                        $"Module available ({why})."));
                else if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
                    checks.Add(DiagCheck.Fail(checkId, $"Module '{module}' imported",
                        $"Module import FAILED ({why}).",
                        evidence: Truncate(modBody),
                        remediation: $"Re-import '{module}' from the PowerShell Gallery into the Automation account " +
                                     "(Modules → Browse gallery, PowerShell 5.1 runtime)."));
                else
                    checks.Add(DiagCheck.Warn(checkId, $"Module '{module}' imported",
                        $"Module import in progress (state '{state}') — usually completes within minutes."));
            }
            else if (modStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && canRunJobs)
            {
                checks.Add(DiagCheck.Warn(checkId, $"Module '{module}' imported",
                    "Cannot verify — this identity lacks module read access (Job Operator level).",
                    remediation: "Grant the Automation Contributor role for full verification, or confirm the module " +
                                 "manually in the Azure portal."));
            }
            else
            {
                checks.Add(DiagCheck.Fail(checkId, $"Module '{module}' imported",
                    modStatus == HttpStatusCode.NotFound
                        ? $"Module not imported into the Automation account ({why})."
                        : $"ARM returned {(int)modStatus} reading the module.",
                    evidence: Truncate(modBody),
                    remediation: $"Import '{module}' from the PowerShell Gallery into the Automation account " +
                                 "(Modules → Browse gallery, PowerShell 5.1 runtime) — the platform-azure Terraform " +
                                 "stack deploys it."));
            }
        }

        return Ok(Finalize(report));
    }

    private static AutomationEnvironmentReport Finalize(AutomationEnvironmentReport report)
    {
        report.PassCount    = report.Checks.Count(c => c.Status == "pass");
        report.FailCount    = report.Checks.Count(c => c.Status == "fail");
        report.WarnCount    = report.Checks.Count(c => c.Status == "warn");
        report.UnknownCount = report.Checks.Count(c => c.Status == "unknown");
        report.IsDeployed   = report.FailCount == 0 && report.UnknownCount == 0;
        report.Summary =
            $"{report.PassCount} pass / {report.FailCount} fail / {report.WarnCount} warn / {report.UnknownCount} unknown";
        return report;
    }

    private static async Task<(HttpStatusCode Status, string Body)> ArmGetAsync(
        HttpClient http, string bearer, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, ct);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Evaluate the caller's effective RBAC from an ARM
    /// Microsoft.Authorization/permissions response: an action is covered when
    /// some permission entry has a matching (wildcard) action not cancelled by
    /// one of that entry's notActions.
    /// </summary>
    private static bool PermissionsCoverAction(string permissionsBody, string action)
    {
        try
        {
            using var doc = JsonDocument.Parse(permissionsBody);
            if (!doc.RootElement.TryGetProperty("value", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var entry in entries.EnumerateArray())
            {
                var allowed = EnumerateStrings(entry, "actions").Any(p => ArmActionMatches(p, action));
                var denied  = EnumerateStrings(entry, "notActions").Any(p => ArmActionMatches(p, action));
                if (allowed && !denied)
                    return true;
            }
        }
        catch (JsonException)
        {
            // Unparseable body — treat as not covered; the caller reports Unknown/Fail with evidence.
        }
        return false;
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(property, out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                yield return s;
    }

    private static bool ArmActionMatches(string pattern, string action) =>
        Regex.IsMatch(action, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);

    private static string? TryGetJsonString(string json, params string[] path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            foreach (var segment in path)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(segment, out el))
                    return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 1000 ? value : value[..1000] + "…";

    // ── Azure Identity (service principal for ARM calls) ─────────────────────

    /// <summary>
    /// Get the current Azure:Identity settings. Secrets and certificate data
    /// are masked (only the last 4 characters are returned) to avoid leaking
    /// sensitive material into the browser.
    /// </summary>
    [HttpGet("azure-identity")]
    public async Task<IActionResult> GetAzureIdentity(CancellationToken ct)
    {
        var section  = _config.GetSection("Azure:Identity");
        var authType = section["AuthMethod"] ?? "secret";
        var secret   = await _resolver.GetAsync("Azure:Identity:ClientSecret", ct) ?? "";
        var certB64  = await _resolver.GetAsync("Azure:Identity:CertificateBase64", ct) ?? "";

        var hasSecret = !string.IsNullOrWhiteSpace(secret);
        var hasCert   = !string.IsNullOrWhiteSpace(certB64);

        var isConfigured = !string.IsNullOrWhiteSpace(section["TenantId"]) &&
                           !string.IsNullOrWhiteSpace(section["ClientId"]) &&
                           (hasSecret || hasCert);

        return Ok(new AzureIdentityResponse(
            TenantId:              section["TenantId"] ?? "",
            ClientId:              section["ClientId"] ?? "",
            AuthMethod:            authType,
            ClientSecretHint:      Mask(secret),
            HasCertificate:        hasCert,
            CertificateThumbprint: section["CertificateThumbprint"] ?? "",
            IsConfigured:          isConfigured));
    }

    /// <summary>
    /// Update the Azure:Identity settings (service principal credentials used
    /// by <c>SpoRestClient</c> to trigger Automation runbooks).
    /// Supports both client-secret and certificate auth.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("azure-identity")]
    public async Task<IActionResult> UpdateAzureIdentity(
        [FromBody] AzureIdentityRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TenantId) || string.IsNullOrWhiteSpace(req.ClientId))
            return BadRequest(new { message = "TenantId and ClientId are required." });

        var method = (req.AuthMethod ?? "secret").ToLowerInvariant();
        if (method == "secret" && string.IsNullOrWhiteSpace(req.ClientSecret))
            return BadRequest(new { message = "Client secret is required when auth method is 'secret'." });
        if (method == "certificate" && string.IsNullOrWhiteSpace(req.CertificateBase64))
            return BadRequest(new { message = "Certificate PFX (base64) is required when auth method is 'certificate'." });

        // 1. Secrets to the store FIRST — if this throws, nothing is half-saved.
        if (method == "secret")
        {
            await _secrets.SetSecretAsync(
                PlatformSecretNames.ForConfigPath("Azure:Identity:ClientSecret"),
                req.ClientSecret!.Trim(), ct);
            await _secrets.DeleteSecretAsync(
                PlatformSecretNames.ForConfigPath("Azure:Identity:CertificateBase64"), ct);
            await _secrets.DeleteSecretAsync(
                PlatformSecretNames.ForConfigPath("Azure:Identity:CertificatePassword"), ct);
        }
        else
        {
            await _secrets.SetSecretAsync(
                PlatformSecretNames.ForConfigPath("Azure:Identity:CertificateBase64"),
                req.CertificateBase64!.Trim(), ct);
            if (!string.IsNullOrEmpty(req.CertificatePassword))
                await _secrets.SetSecretAsync(
                    PlatformSecretNames.ForConfigPath("Azure:Identity:CertificatePassword"),
                    req.CertificatePassword.Trim(), ct);
            else
                await _secrets.DeleteSecretAsync(
                    PlatformSecretNames.ForConfigPath("Azure:Identity:CertificatePassword"), ct);
            await _secrets.DeleteSecretAsync(
                PlatformSecretNames.ForConfigPath("Azure:Identity:ClientSecret"), ct);
        }

        // 2. Non-secret fields (+ kv: markers in Key Vault mode) into the file.
        //    In file mode the store already wrote the plaintext values at these
        //    paths; only replace the non-secret fields then.
        await SettingsOverrideFile.UpdateAsync(OverridePath, root =>
        {
            var azure = root["Azure"] as JsonObject ?? new JsonObject();
            root["Azure"] = azure;
            var node = azure["Identity"] as JsonObject ?? new JsonObject();
            azure["Identity"] = node;

            node["TenantId"]   = req.TenantId.Trim();
            node["ClientId"]   = req.ClientId.Trim();
            node["AuthMethod"] = method;

            if (method == "secret")
            {
                if (_secrets.IsExternal)
                    node["ClientSecret"] = PlatformSecretNames.Marker +
                        PlatformSecretNames.ForConfigPath("Azure:Identity:ClientSecret");
                node.Remove("CertificateBase64");
                node.Remove("CertificatePassword");
                node.Remove("CertificateThumbprint");
            }
            else
            {
                if (_secrets.IsExternal)
                {
                    node["CertificateBase64"] = PlatformSecretNames.Marker +
                        PlatformSecretNames.ForConfigPath("Azure:Identity:CertificateBase64");
                    if (!string.IsNullOrEmpty(req.CertificatePassword))
                        node["CertificatePassword"] = PlatformSecretNames.Marker +
                            PlatformSecretNames.ForConfigPath("Azure:Identity:CertificatePassword");
                    else
                        node.Remove("CertificatePassword");
                }
                node["CertificateThumbprint"] = req.CertificateThumbprint?.Trim() ?? "";
                node.Remove("ClientSecret");
            }
        }, ct);

        _resolver.Invalidate("Azure:Identity:ClientSecret");
        _resolver.Invalidate("Azure:Identity:CertificateBase64");
        _resolver.Invalidate("Azure:Identity:CertificatePassword");

        _logger.LogInformation(
            "Azure:Identity (service principal, method={Method}) updated via API. Secret storage: {Storage}.",
            method, _secrets.IsExternal ? "Key Vault" : "settings.override.json");

        // Live-probe the saved credential with an ARM token mint so a pasted
        // object id, a foreign app's secret, or a typo'd tenant surfaces NOW
        // instead of as an opaque "not deployed" verification later (a live
        // setup run saved an SP object id as ClientId + the wrong app's secret
        // and lost an hour to it). Best-effort: probe failure never blocks the
        // save — the result rides back on the response for the UI to show.
        var credentialTest = await TestArmCredentialAsync(req, method, ct);

        return Ok(new AzureIdentityResponse(
            TenantId:              req.TenantId.Trim(),
            ClientId:              req.ClientId.Trim(),
            AuthMethod:            method,
            ClientSecretHint:      method == "secret" ? Mask(req.ClientSecret!) : "",
            HasCertificate:        method == "certificate",
            CertificateThumbprint: req.CertificateThumbprint ?? "",
            IsConfigured:          true,
            CredentialTest:        credentialTest));
    }

    private static async Task<CredentialTestResult> TestArmCredentialAsync(
        AzureIdentityRequest req, string method, CancellationToken ct)
    {
        try
        {
            Azure.Core.TokenCredential cred;
            if (method == "secret")
            {
                cred = new Azure.Identity.ClientSecretCredential(
                    req.TenantId.Trim(), req.ClientId.Trim(), req.ClientSecret!.Trim());
            }
            else
            {
                var pfx = Convert.FromBase64String(req.CertificateBase64!.Trim());
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    pfx, req.CertificatePassword,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
                cred = new Azure.Identity.ClientCertificateCredential(
                    req.TenantId.Trim(), req.ClientId.Trim(), cert);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await cred.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                timeout.Token);
            return new CredentialTestResult(true, null);
        }
        catch (Exception ex)
        {
            // First line of the AAD error is the useful part (AADSTS code + text).
            var firstLine = ex.Message.Split('\n')[0].Trim();
            return new CredentialTestResult(false, firstLine);
        }
    }

    /// <summary>Remove the stored Azure:Identity credentials.</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("azure-identity")]
    public async Task<IActionResult> DeleteAzureIdentity(CancellationToken ct)
    {
        await _secrets.DeleteSecretAsync(
            PlatformSecretNames.ForConfigPath("Azure:Identity:ClientSecret"), ct);
        await _secrets.DeleteSecretAsync(
            PlatformSecretNames.ForConfigPath("Azure:Identity:CertificateBase64"), ct);
        await _secrets.DeleteSecretAsync(
            PlatformSecretNames.ForConfigPath("Azure:Identity:CertificatePassword"), ct);

        await SettingsOverrideFile.UpdateAsync(OverridePath, root =>
        {
            if (root["Azure"] is JsonObject azure)
                azure.Remove("Identity");
        }, ct);

        _resolver.Invalidate("Azure:Identity:ClientSecret");
        _resolver.Invalidate("Azure:Identity:CertificateBase64");
        _resolver.Invalidate("Azure:Identity:CertificatePassword");

        _logger.LogInformation("Azure:Identity (service principal) removed via API.");
        return Ok(new AzureIdentityResponse("", "", "secret", "", false, "", false));
    }

    // ── Cross-tenant mailbox migration app ───────────────────────────────────

    /// <summary>
    /// Get the cross-tenant Mailbox Migration app settings. The client secret
    /// is masked (last 4 characters) — it is write-only via PUT.
    /// </summary>
    [HttpGet("cross-tenant-migration")]
    public async Task<IActionResult> GetCrossTenantMigration(CancellationToken ct)
    {
        var appId  = _config["Platform:CrossTenantMigration:AppId"] ?? "";
        var secret = await _resolver.GetAsync("Platform:CrossTenantMigration:ClientSecret", ct) ?? "";

        return Ok(new CrossTenantMigrationResponse(
            AppId:            appId,
            ClientSecretHint: Mask(secret),
            IsConfigured:     !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(secret)));
    }

    /// <summary>
    /// Update the cross-tenant Mailbox Migration app settings
    /// (Platform:CrossTenantMigration). An empty <c>ClientSecret</c> keeps the
    /// currently stored secret so the AppId can be changed independently.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("cross-tenant-migration")]
    public async Task<IActionResult> UpdateCrossTenantMigration(
        [FromBody] CrossTenantMigrationRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.AppId) || !Guid.TryParse(req.AppId.Trim(), out _))
            return BadRequest(new
            {
                message = "AppId must be the migration app's Application (client) ID — a GUID from the app " +
                          "registration's Overview blade (not the enterprise-app object ID).",
            });

        var newSecret = string.IsNullOrWhiteSpace(req.ClientSecret) ? null : req.ClientSecret.Trim();

        if (newSecret is not null)
            await _secrets.SetSecretAsync(
                PlatformSecretNames.ForConfigPath("Platform:CrossTenantMigration:ClientSecret"),
                newSecret, ct);

        await SettingsOverrideFile.UpdateAsync(OverridePath, root =>
        {
            var platform = root["Platform"] as JsonObject ?? new JsonObject();
            root["Platform"] = platform;
            var node = platform["CrossTenantMigration"] as JsonObject ?? new JsonObject();
            platform["CrossTenantMigration"] = node;

            node["AppId"] = req.AppId.Trim();
            if (newSecret is not null && _secrets.IsExternal)
                node["ClientSecret"] = PlatformSecretNames.Marker +
                    PlatformSecretNames.ForConfigPath("Platform:CrossTenantMigration:ClientSecret");
        }, ct);

        if (newSecret is not null)
            _resolver.Invalidate("Platform:CrossTenantMigration:ClientSecret");

        _logger.LogInformation(
            "Platform:CrossTenantMigration updated via API (AppId {AppId}, secret {SecretAction}; storage: {Storage}).",
            req.AppId.Trim(),
            newSecret is null ? "unchanged" : "replaced",
            _secrets.IsExternal ? "Key Vault" : "settings.override.json");

        var effectiveSecret = newSecret
            ?? await _resolver.GetAsync("Platform:CrossTenantMigration:ClientSecret", ct)
            ?? "";
        return Ok(new CrossTenantMigrationResponse(
            AppId:            req.AppId.Trim(),
            ClientSecretHint: Mask(effectiveSecret),
            IsConfigured:     !string.IsNullOrWhiteSpace(effectiveSecret)));
    }

    private static string Mask(string value) =>
        value.Length >= 4 ? $"****{value[^4..]}" : (value.Length > 0 ? "****" : "");
}

public record AzureAutomationSettings(
    string SubscriptionId,
    string ResourceGroup,
    string AccountName,
    string RunbookName,
    int    JobPollIntervalSeconds,
    int    JobTimeoutMinutes);

public record AzureIdentityRequest(
    string TenantId,
    string ClientId,
    string? AuthMethod,
    string? ClientSecret,
    string? CertificateBase64,
    string? CertificatePassword,
    string? CertificateThumbprint);

public record AzureIdentityResponse(
    string TenantId,
    string ClientId,
    string AuthMethod,
    string ClientSecretHint,
    bool   HasCertificate,
    string CertificateThumbprint,
    bool   IsConfigured,
    CredentialTestResult? CredentialTest = null);

/// <summary>Outcome of the save-time live credential probe (an ARM token mint).</summary>
public record CredentialTestResult(bool Success, string? Error);

public record CrossTenantMigrationRequest(
    string  AppId,
    string? ClientSecret);

public record CrossTenantMigrationResponse(
    string AppId,
    string ClientSecretHint,
    bool   IsConfigured);

/// <summary>
/// Result of the live Azure Automation environment scan
/// (<c>POST api/settings/azure-automation/verify</c>).
/// </summary>
public sealed class AutomationEnvironmentReport
{
    public DateTime GeneratedAt { get; set; }
    public bool IsDeployed { get; set; }
    public string Summary { get; set; } = "";
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int WarnCount { get; set; }
    public int UnknownCount { get; set; }
    public List<DiagCheck> Checks { get; set; } = new();
}
