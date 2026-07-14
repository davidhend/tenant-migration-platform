using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Services.Graph;

/// <summary>
/// Probes Entra cross-tenant synchronization (CTS) configuration. CTS is a
/// push model: the configuration (sync app + sync job) lives in the
/// <b>source</b> tenant and provisions users into the target tenant. The
/// target tenant only contributes the inbound cross-tenant access policy.
/// Strategy:
/// <list type="number">
///   <item><description>In the <b>source</b> tenant: page through service
///   principals and look for one with a synchronization job whose
///   <c>templateId</c> matches a known cross-tenant sync template.</description></item>
///   <item><description>In the <b>target</b> tenant: read the partner policy at
///   <c>/policies/crossTenantAccessPolicy/partners/{sourceTenantId}</c> to
///   confirm inbound user sync is allowed.</description></item>
/// </list>
/// We can't read synchronization secrets via Graph (they're write-only), so
/// we don't try to confirm the source-side job points at this specific target
/// tenant — we report the discovered job's display name instead so the admin
/// can confirm by sight.
/// </summary>
public sealed class CrossTenantSyncDiscoveryService : ICrossTenantSyncDiscoveryService
{
    private readonly IGraphClientFactory _graphFactory;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CrossTenantSyncDiscoveryService> _logger;

    // Bound the per-call enumeration so a tenant with thousands of SPs doesn't
    // hang the dependency check. CTS apps are typically created early, so a
    // few hundred is plenty for source tenants involved in a migration.
    private const int MaxServicePrincipalsScanned = 500;

    // Cross-tenant sync template IDs vary by gallery vintage:
    // - GA apps use "Azure2Azure"
    // - Preview gallery apps ("Cross-Tenant Synchronization Preview") commonly
    //   carry "Azure2AzureProvisioning" or "Azure2Azure_Preview"
    // Match by case-insensitive prefix so all variants are accepted.
    private const string CrossTenantSyncTemplateIdPrefix = "Azure2Azure";

    private static bool IsCrossTenantSyncTemplate(string? templateId) =>
        !string.IsNullOrEmpty(templateId) &&
        templateId.StartsWith(CrossTenantSyncTemplateIdPrefix, StringComparison.OrdinalIgnoreCase);

    // DisplayName fragments used by Entra's cross-tenant sync gallery templates.
    // We pre-filter SPs by these patterns so we only probe /synchronization/jobs
    // (which requires Synchronization.ReadWrite.All) on apps that are actually
    // candidates — turning a 500-SP scan that 401s on every probe into ~1 probe.
    private static readonly string[] CrossTenantSyncDisplayNameFragments =
    {
        "Cross-Tenant Synchronization", // GA + Preview gallery names
        "Cross Tenant Synchronization",
        "CrossTenantSync",
        "Cross-Tenant Sync",
    };

    private static bool LooksLikeCrossTenantSyncApp(string? displayName) =>
        !string.IsNullOrEmpty(displayName) &&
        CrossTenantSyncDisplayNameFragments.Any(f =>
            displayName.Contains(f, StringComparison.OrdinalIgnoreCase));

    public CrossTenantSyncDiscoveryService(
        IGraphClientFactory graphFactory,
        IKeyVaultCredentialService keyVault,
        IConfiguration configuration,
        ILogger<CrossTenantSyncDiscoveryService> logger)
    {
        _graphFactory = graphFactory;
        _keyVault = keyVault;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CrossTenantSyncDiscoveryResult> DiscoverAsync(
        Tenant sourceTenant,
        Tenant targetTenant,
        CancellationToken ct = default)
    {
        if (_configuration.GetValue<bool>("Platform:MockGraphCalls"))
        {
            return new CrossTenantSyncDiscoveryResult(
                IsConfigured: true,
                PartnerPolicyConfigured: true,
                ServicePrincipalId: Guid.NewGuid().ToString(),
                ServicePrincipalDisplayName: $"Mock Cross-Tenant Sync → {targetTenant.DisplayName}",
                SyncJobId: $"Azure2Azure.{Guid.NewGuid():N}.{Guid.NewGuid():N}",
                SyncJobTemplateId: "Azure2Azure",
                SyncJobStatus: "Active",
                LastSyncAt: DateTimeOffset.UtcNow.AddMinutes(-7),
                Message: "Mock mode — discovery returns a synthetic configured result.",
                Remediation: null,
                Error: null);
        }

        // ── 1. Source tenant: look for the sync app + job ───────────────────
        GraphServiceClient sourceGraph;
        try
        {
            var (cert, certPw, secret) = await _keyVault.LoadCredentialsAsync(sourceTenant.Id, ct);
            sourceGraph = _graphFactory.CreateForTenant(sourceTenant, cert, certPw, secret);
        }
        catch (Exception ex)
        {
            return Failure($"Source tenant credentials not available: {ex.Message}",
                "Open Tenants → Source tenant → Re-configure App.", ex.Message);
        }

        string? matchedSpId = null;
        string? matchedSpName = null;
        string? matchedJobId = null;
        string? matchedJobTemplateId = null;
        string? matchedJobStatus = null;
        DateTimeOffset? matchedLastSync = null;
        string? sourceProbeError = null;
        var scannedCandidates = new List<ServicePrincipal>();

        // Operator may pin the SP that hosts the cross-tenant sync job — bypasses the
        // SP scan entirely and works around (a) preview-template templateIds the scan
        // doesn't recognize, (b) tenants with >500 SPs, and (c) cases where Graph's
        // /synchronization/jobs returns 404 for SPs the caller can't read.
        var pinnedSpId = _configuration["Platform:CrossTenantSync:SourceServicePrincipalObjectId"];

        try
        {
            if (!string.IsNullOrWhiteSpace(pinnedSpId))
            {
                _logger.LogInformation(
                    "Cross-tenant sync discovery: probing pinned SP {SpId} in source tenant {SourceTenantId} (Platform:CrossTenantSync:SourceServicePrincipalObjectId).",
                    pinnedSpId, sourceTenant.TenantId);

                ServicePrincipal? sp = null;
                try
                {
                    sp = await sourceGraph.ServicePrincipals[pinnedSpId]
                        .GetAsync(req =>
                        {
                            req.QueryParameters.Select = ["id", "displayName", "appId", "appDisplayName"];
                        }, ct);
                }
                catch (ODataError ex)
                {
                    sourceProbeError =
                        $"Pinned cross-tenant sync SP '{pinnedSpId}' could not be read: {ex.Error?.Message ?? ex.Message}";
                    _logger.LogWarning(ex,
                        "Cross-tenant sync discovery: pinned SP {SpId} not readable in source tenant {SourceTenantId}.",
                        pinnedSpId, sourceTenant.TenantId);
                }

                if (sp is not null)
                {
                    scannedCandidates.Add(sp);
                    SynchronizationJobCollectionResponse? jobs = null;
                    try
                    {
                        jobs = await sourceGraph.ServicePrincipals[pinnedSpId]
                            .Synchronization.Jobs
                            .GetAsync(req =>
                            {
                                req.QueryParameters.Select = ["id", "templateId", "status", "schedule"];
                            }, ct);
                    }
                    catch (ODataError ex)
                    {
                        sourceProbeError =
                            $"Reading synchronization jobs on pinned SP '{pinnedSpId}' failed: {ex.Error?.Message ?? ex.Message}";
                        _logger.LogWarning(ex,
                            "Cross-tenant sync discovery: cannot enumerate jobs on pinned SP {SpId}.",
                            pinnedSpId);
                    }

                    // Pinned path: accept ANY synchronization job — the operator vouched
                    // for this SP, so we don't gate on templateId.
                    var job = jobs?.Value?.FirstOrDefault(j => IsCrossTenantSyncTemplate(j.TemplateId))
                           ?? jobs?.Value?.FirstOrDefault();
                    if (job is not null)
                    {
                        matchedSpId = sp.Id;
                        matchedSpName = sp.DisplayName;
                        matchedJobId = job.Id;
                        matchedJobTemplateId = job.TemplateId;
                        matchedJobStatus = job.Status?.Code?.ToString();
                        matchedLastSync = job.Status?.LastSuccessfulExecution?.TimeEnded
                                       ?? job.Status?.LastExecution?.TimeEnded;
                        _logger.LogInformation(
                            "Cross-tenant sync discovery: matched pinned SP '{Name}' ({SpId}) job {JobId} (template {Template}, status {Status}).",
                            sp.DisplayName, sp.Id, job.Id, job.TemplateId, matchedJobStatus ?? "unknown");
                    }
                    else if (sourceProbeError is null)
                    {
                        sourceProbeError =
                            $"Pinned SP '{sp.DisplayName}' ({pinnedSpId}) has no synchronization jobs. " +
                            "Open Entra → Enterprise Applications → that app → Provisioning, then start a configuration.";
                    }
                }
            }
            else
            {
                // Pre-filter the SP listing to apps whose displayName looks like a
                // cross-tenant sync app. This both narrows the scan to a handful of
                // candidates AND keeps the noisy /synchronization/jobs probes (which
                // require Synchronization.ReadWrite.All) bounded — without the filter,
                // a tenant where that permission is missing produces a 401 storm
                // across hundreds of unrelated SPs.
                var candidates = new List<ServicePrincipal>();
                var page = await sourceGraph.ServicePrincipals.GetAsync(req =>
                {
                    req.QueryParameters.Select = ["id", "displayName", "appId", "appDisplayName"];
                    req.QueryParameters.Top = 100;
                }, ct);

                int probed = 0;
                while (page?.Value is not null && probed < MaxServicePrincipalsScanned)
                {
                    foreach (var sp in page.Value)
                    {
                        probed++;
                        if (probed >= MaxServicePrincipalsScanned) break;
                        if (string.IsNullOrWhiteSpace(sp.Id)) continue;
                        if (LooksLikeCrossTenantSyncApp(sp.DisplayName))
                        {
                            candidates.Add(sp);
                            scannedCandidates.Add(sp);
                        }
                    }
                    if (string.IsNullOrEmpty(page.OdataNextLink) || probed >= MaxServicePrincipalsScanned) break;
                    page = await sourceGraph.ServicePrincipals
                        .WithUrl(page.OdataNextLink)
                        .GetAsync(cancellationToken: ct);
                }

                _logger.LogInformation(
                    "Cross-tenant sync discovery: scanned {Probed} SP(s) in source tenant {SourceTenantId}; {Candidates} matched name pattern.",
                    probed, sourceTenant.TenantId, candidates.Count);

                int jobsListErrors = 0;
                int authErrors = 0;
                string? lastAuthErrorMessage = null;
                foreach (var sp in candidates)
                {
                    if (matchedJobId is not null) break;

                    SynchronizationJobCollectionResponse? jobs;
                    try
                    {
                        jobs = await sourceGraph.ServicePrincipals[sp.Id!]
                            .Synchronization.Jobs
                            .GetAsync(req =>
                            {
                                req.QueryParameters.Select = ["id", "templateId", "status", "schedule"];
                            }, ct);
                    }
                    catch (ODataError ex)
                    {
                        jobsListErrors++;
                        if (ex.ResponseStatusCode is 401 or 403)
                        {
                            authErrors++;
                            lastAuthErrorMessage = ex.Error?.Message ?? ex.Message;
                        }
                        _logger.LogDebug(ex,
                            "Cross-tenant sync discovery: jobs/list on SP '{Name}' ({SpId}) returned {Status} {Code} — skipping.",
                            sp.DisplayName, sp.Id, ex.ResponseStatusCode, ex.Error?.Code);
                        continue;
                    }

                    // Pinned-app semantics: when an SP looks like a cross-tenant sync
                    // app by displayName, accept ANY synchronization job on it — the
                    // gallery name is more reliable than templateId across vintages.
                    var job = jobs?.Value?.FirstOrDefault(j => IsCrossTenantSyncTemplate(j.TemplateId))
                           ?? jobs?.Value?.FirstOrDefault();
                    if (job is null) continue;

                    matchedSpId = sp.Id;
                    matchedSpName = sp.DisplayName;
                    matchedJobId = job.Id;
                    matchedJobTemplateId = job.TemplateId;
                    matchedJobStatus = job.Status?.Code?.ToString();
                    matchedLastSync = job.Status?.LastSuccessfulExecution?.TimeEnded
                                   ?? job.Status?.LastExecution?.TimeEnded;
                    _logger.LogInformation(
                        "Cross-tenant sync discovery: matched SP '{Name}' ({SpId}) job {JobId} (template {Template}).",
                        sp.DisplayName, sp.Id, job.Id, job.TemplateId);
                }

                // If we never found a job AND every candidate's jobs/list returned
                // 401/403, the scan didn't fail — the app reg lacks the required
                // permission. Promote that to a specific actionable error.
                if (matchedJobId is null && candidates.Count > 0 &&
                    authErrors == candidates.Count)
                {
                    sourceProbeError =
                        $"Source-tenant app registration is missing the Graph permission to read synchronization jobs " +
                        $"({lastAuthErrorMessage ?? "401/403 on /servicePrincipals/{id}/synchronization/jobs"}). " +
                        $"Found {candidates.Count} candidate cross-tenant sync app(s) by display name, but every jobs/list returned {(authErrors == candidates.Count ? "401/403" : "an error")}.";
                    _logger.LogWarning(
                        "Cross-tenant sync discovery: {AuthErrors}/{Candidates} candidates returned 401/403 on jobs/list — likely missing Synchronization.ReadWrite.All on source app registration.",
                        authErrors, candidates.Count);
                }
                else if (matchedJobId is null && candidates.Count == 0)
                {
                    _logger.LogInformation(
                        "Cross-tenant sync discovery: scanned {Probed} SP(s); none matched the cross-tenant sync display-name pattern.",
                        probed);
                }
            }
        }
        catch (Exception ex)
        {
            sourceProbeError = ex.Message;
            _logger.LogWarning(ex,
                "Cross-tenant sync discovery: error enumerating service principals in source tenant {SourceTenantId}.",
                sourceTenant.TenantId);
        }

        // ── 2. Target tenant: confirm inbound partner policy ────────────────
        bool partnerPolicyConfigured = false;
        string? partnerProbeError = null;
        try
        {
            var (cert, certPw, secret) = await _keyVault.LoadCredentialsAsync(targetTenant.Id, ct);
            var targetGraph = _graphFactory.CreateForTenant(targetTenant, cert, certPw, secret);

            try
            {
                var partner = await targetGraph.Policies.CrossTenantAccessPolicy
                    .Partners[sourceTenant.TenantId]
                    .GetAsync(cancellationToken: ct);
                partnerPolicyConfigured = partner is not null;
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                partnerPolicyConfigured = false;
            }
        }
        catch (Exception ex)
        {
            partnerProbeError = ex.Message;
            _logger.LogWarning(ex,
                "Cross-tenant sync discovery: failed to read inbound partner policy for source {SourceTenantId} on target {TargetTenantId}.",
                sourceTenant.TenantId, targetTenant.TenantId);
        }

        // ── 3. Compose final result ─────────────────────────────────────────
        // "Configured" requires both: a CTS sync job in the source tenant AND
        // the inbound partner policy in the target tenant. We can't verify that
        // the discovered source-side job specifically targets THIS target — the
        // admin can confirm by sight using the displayName.
        var configured = matchedJobId is not null && partnerPolicyConfigured;

        string message;
        string? remediation;
        string? error = sourceProbeError ?? partnerProbeError;

        if (sourceProbeError is not null && matchedJobId is null)
        {
            message = sourceProbeError;
            if (scannedCandidates.Count > 0)
            {
                message += " Candidate(s): " + string.Join("; ", scannedCandidates.Select(c =>
                    $"'{c.DisplayName}' (appId {c.AppId ?? "unknown"}, enterprise-app objectId {c.Id})")) + ".";
            }
            remediation =
                "On the SOURCE tenant's platform app registration, grant the Microsoft Graph application permissions " +
                "'Synchronization.ReadWrite.All' (or 'Application.ReadWrite.OwnedBy') AND 'Application.Read.All', " +
                "then click 'Grant admin consent'. This unblocks /servicePrincipals/{id}/synchronization/jobs.";
        }
        else if (configured)
        {
            message = $"Sync app '{matchedSpName}' (job {Truncate(matchedJobId, 12)}…) configured in source tenant. Status: {matchedJobStatus ?? "unknown"}. " +
                      "Confirm by sight that this job targets the right tenant — Graph doesn't expose the binding.";
            remediation = null;
        }
        else if (matchedJobId is not null && !partnerPolicyConfigured && partnerProbeError is null)
        {
            message = $"Sync app '{matchedSpName}' found in source tenant, but the target tenant has no inbound partner policy for source {sourceTenant.TenantId}.";
            remediation = $"In the TARGET tenant: Entra ID → External Identities → Cross-tenant access settings → Add organization → enter source tenant ID {sourceTenant.TenantId}, then enable inbound user sync.";
        }
        else if (matchedJobId is not null && partnerProbeError is not null)
        {
            message = $"Sync app '{matchedSpName}' found in source tenant. Could not check the target tenant's inbound partner policy — {partnerProbeError}.";
            remediation = "Confirm the TARGET tenant app registration has the Policy.Read.All permission with admin consent granted.";
        }
        else if (matchedJobId is null && partnerPolicyConfigured)
        {
            message = $"Inbound partner policy exists in target tenant, but no cross-tenant sync job was found in the source tenant. Scanned up to {MaxServicePrincipalsScanned} service principals; matched template prefix '{CrossTenantSyncTemplateIdPrefix}*'.";
            remediation = "If the cross-tenant sync app exists but discovery missed it, set Platform:CrossTenantSync:SourceServicePrincipalObjectId in appsettings.json to its enterprise-app object ID. " +
                          "Otherwise, in the SOURCE tenant: Entra ID → Cross-tenant synchronization → Configurations → New configuration → enter the target tenant ID, create a sync job, and start provisioning.";
        }
        else
        {
            message = $"Neither a sync job in the source tenant (template prefix '{CrossTenantSyncTemplateIdPrefix}*') nor an inbound partner policy in the target tenant was found.";
            remediation = "In the SOURCE tenant: Entra ID → Cross-tenant synchronization → New configuration → target tenant ID. " +
                          "In the TARGET tenant: Entra ID → External Identities → Cross-tenant access settings → Add organization → source tenant ID, enable inbound user sync. " +
                          "If the source-side app exists but auto-discovery doesn't see it, pin it via Platform:CrossTenantSync:SourceServicePrincipalObjectId.";
        }

        return new CrossTenantSyncDiscoveryResult(
            IsConfigured: configured,
            PartnerPolicyConfigured: partnerPolicyConfigured,
            ServicePrincipalId: matchedSpId,
            ServicePrincipalDisplayName: matchedSpName,
            SyncJobId: matchedJobId,
            SyncJobTemplateId: matchedJobTemplateId,
            SyncJobStatus: matchedJobStatus,
            LastSyncAt: matchedLastSync,
            Message: message,
            Remediation: remediation,
            Error: error,
            Candidates: scannedCandidates.Count == 0 ? null : scannedCandidates
                .Select(sp => new CrossTenantSyncCandidate(sp.DisplayName, sp.AppId, sp.Id))
                .ToList());
    }

    private static CrossTenantSyncDiscoveryResult Failure(string message, string? remediation, string? error) =>
        new(
            IsConfigured: false,
            PartnerPolicyConfigured: false,
            ServicePrincipalId: null,
            ServicePrincipalDisplayName: null,
            SyncJobId: null,
            SyncJobTemplateId: null,
            SyncJobStatus: null,
            LastSyncAt: null,
            Message: message,
            Remediation: remediation,
            Error: error);

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max];
}
