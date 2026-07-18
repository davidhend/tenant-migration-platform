using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _projects;
    private readonly ITenantRepository _tenants;
    private readonly IAuditRepository _audit;
    private readonly IExoRestClient _exoClient;
    private readonly ITenantCredentialFactory _credentialFactory;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly IScanRepository _scans;
    private readonly IIdentityMapRepository _identityMaps;
    private readonly IWaveRepository _waves;
    private readonly IUserMigrationRepository _userMigrations;
    private readonly ICrossTenantSyncDiscoveryService _ctsDiscovery;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(
        IProjectRepository projects,
        ITenantRepository tenants,
        IAuditRepository audit,
        IExoRestClient exoClient,
        ITenantCredentialFactory credentialFactory,
        IKeyVaultCredentialService keyVault,
        IScanRepository scans,
        IIdentityMapRepository identityMaps,
        IWaveRepository waves,
        IUserMigrationRepository userMigrations,
        ICrossTenantSyncDiscoveryService ctsDiscovery,
        IConfiguration configuration,
        ICurrentUserService currentUser,
        ILogger<ProjectsController> logger)
    {
        _projects = projects;
        _tenants = tenants;
        _audit = audit;
        _exoClient = exoClient;
        _credentialFactory = credentialFactory;
        _keyVault = keyVault;
        _scans = scans;
        _identityMaps = identityMaps;
        _waves = waves;
        _userMigrations = userMigrations;
        _ctsDiscovery = ctsDiscovery;
        _configuration = configuration;
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await _projects.GetAllWithTenantsAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(id, ct);
        return project is null ? NotFound() : Ok(project);
    }

    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest req, CancellationToken ct)
    {
        if (!await _tenants.ExistsAsync(req.SourceTenantId, ct))
            return BadRequest("Source tenant not found.");
        if (!await _tenants.ExistsAsync(req.TargetTenantId, ct))
            return BadRequest("Target tenant not found.");

        var project = new MigrationProject
        {
            Name = req.Name,
            SourceTenantId = req.SourceTenantId,
            TargetTenantId = req.TargetTenantId,
            Status = ProjectStatus.Draft,
        };

        await _projects.AddAsync(project, ct);
        await _projects.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "PROJECT_CREATED",
            Resource = $"projects/{project.Id}",
            Actor = _currentUser.UserName,
            ProjectId = project.Id,
            Details = $$$"""{"name":"{{{project.Name}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        var enriched = await _projects.GetByIdWithTenantsAsync(project.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = project.Id }, enriched);
    }

    /// <summary>
    /// Automates the Exchange Online cross-tenant migration prerequisites for this project:
    /// <list type="bullet">
    ///   <item>Creates an outbound organization relationship in the source tenant (if absent)</item>
    ///   <item>Creates an inbound organization relationship in the target tenant (if absent)</item>
    ///   <item>Creates a cross-tenant migration endpoint in the source tenant (if absent)</item>
    /// </list>
    /// All steps are idempotent — running this multiple times is safe.
    /// Requires <c>Exchange.ManageAsApp</c> permission and sufficient Exchange management roles
    /// (<c>Federated Sharing</c>, <c>Migration</c>, <c>Mail Recipients</c>) on both
    /// the source and target tenant app registrations.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{id:guid}/setup-exchange")]
    public async Task<IActionResult> SetupExchange(Guid id, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(id, ct);
        if (project is null) return NotFound();

        var source = project.SourceTenant;
        var target = project.TargetTenant;

        if (source is null || target is null)
            return UnprocessableEntity(new { message = "Project source or target tenant is not loaded." });

        // ── Mock mode: return synthetic success without calling EXO ──────────
        // When Platform:MockGraphCalls=true (dev / CI without real tenant creds)
        // we skip all real EXO REST calls and return a deterministic mock response.
        // This mirrors the pattern used by the scan pipeline scanners.
        if (_configuration.GetValue<bool>("Platform:MockGraphCalls"))
        {
            _logger.LogInformation(
                "MockGraphCalls=true — skipping real EXO setup for project {Id}. " +
                "Returning synthetic success response.", id);

            var mockSourceDomain = string.IsNullOrWhiteSpace(source.OnMicrosoftDomain)
                ? "source.onmicrosoft.com"
                : $"{source.OnMicrosoftDomain}.onmicrosoft.com";
            var mockTargetDomain = string.IsNullOrWhiteSpace(target.OnMicrosoftDomain)
                ? "target.onmicrosoft.com"
                : $"{target.OnMicrosoftDomain}.onmicrosoft.com";

            await _audit.AddAsync(new AuditEvent
            {
                Action    = "PROJECT_EXCHANGE_SETUP",
                Resource  = $"projects/{id}",
                Actor     = _currentUser.UserName,
                ProjectId = id,
                Details   = """{"sourceOrgRelCreated":true,"targetOrgRelCreated":true,"endpointCreated":true,"endpointIdentity":"CrossTenantMigration","mock":true}""",
            }, ct);
            await _audit.SaveAsync(ct);

            return Ok(new
            {
                sourceOrgRelationship = new { status = "created", domain = mockTargetDomain, error = (string?)null },
                targetOrgRelationship = new { status = "created", domain = mockSourceDomain, error = (string?)null },
                migrationEndpoint     = new { status = "created", identity = "CrossTenantMigration", error = (string?)null },
                warnings              = Array.Empty<string>(),
                mock                  = true,
            });
        }

        // Derive the onmicrosoft.com domains used for org relationship domain names.
        var sourceDomain = string.IsNullOrWhiteSpace(source.OnMicrosoftDomain)
            ? null : $"{source.OnMicrosoftDomain}.onmicrosoft.com";
        var targetDomain = string.IsNullOrWhiteSpace(target.OnMicrosoftDomain)
            ? null : $"{target.OnMicrosoftDomain}.onmicrosoft.com";

        if (sourceDomain is null)
            return UnprocessableEntity(new
            {
                message = "Source tenant onmicrosoft.com domain is not set. " +
                          "Verify the source tenant via Tenants → Verify Connection to auto-detect it."
            });

        if (targetDomain is null)
            return UnprocessableEntity(new
            {
                message = "Target tenant onmicrosoft.com domain is not set. " +
                          "Verify the target tenant via Tenants → Verify Connection to auto-detect it."
            });

        // Load credentials for both tenants.
        var (srcCertB64, srcCertPw, srcSecret) = await _keyVault.LoadCredentialsAsync(source.Id, ct);
        var (tgtCertB64, tgtCertPw, tgtSecret) = await _keyVault.LoadCredentialsAsync(target.Id, ct);

        Azure.Core.TokenCredential sourceCredential, targetCredential;
        try
        {
            sourceCredential = _credentialFactory.CreateCredential(source, srcCertB64, srcCertPw, srcSecret);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = $"Source tenant credentials not available: {ex.Message}" });
        }

        try
        {
            targetCredential = _credentialFactory.CreateCredential(target, tgtCertB64, tgtCertPw, tgtSecret);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = $"Target tenant credentials not available: {ex.Message}" });
        }

        // The OAuthApplicationId must be stamped on both org relationships AND the migration
        // endpoint. Read it once here so it flows consistently into all three steps.
        // Falls back to null if not configured — EnsureOrganizationRelationshipAsync will
        // skip stamping OAuthApplicationId rather than crash.
        var crossTenantAppId = _configuration["Platform:CrossTenantMigration:AppId"];

        // Each step tracks its own status: "created" | "existing" | "failed"
        string srcOrgStatus = "failed", tgtOrgStatus = "failed", epStatus = "failed";
        string? srcOrgError = null, tgtOrgError = null, epError = null;
        string endpointIdentity = string.Empty;

        // The scope DG that the migration worker will create on source for this tenant pair.
        // MUST match MailboxMigrationWorker's scopeGroupName, otherwise MRS rejects every move
        // with `0x80070057` because the migrating user isn't in the relationship's published scope.
        // EXO defaults to a literal "Migration Users" group if MailboxMovePublishedScopes is null —
        // pass the real scope name explicitly to avoid that landmine.
        var scopeGroupName = $"CTMS-{target.OnMicrosoftDomain}";

        // Step 1 — Source tenant: outbound org relationship.
        try
        {
            var created = await _exoClient.EnsureOrganizationRelationshipAsync(
                source.TenantId.ToString(),
                targetDomain,
                $"CrossTenantMigration-{target.DisplayName}",
                "Outbound",
                sourceCredential,
                ct,
                oauthApplicationId: crossTenantAppId,
                mailboxMovePublishedScopes: scopeGroupName,
                partnerTenantId: target.TenantId.ToString());
            srcOrgStatus = created ? "created" : "existing";
        }
        catch (InvalidOperationException ex)
        {
            srcOrgError = ex.Message;
            _logger.LogWarning(ex, "Exchange setup: failed to ensure source org relationship for project {Id}.", id);
        }

        // Step 2 — Target tenant: inbound org relationship.
        try
        {
            var created = await _exoClient.EnsureOrganizationRelationshipAsync(
                target.TenantId.ToString(),
                sourceDomain,
                $"CrossTenantMigration-{source.DisplayName}",
                "Inbound",
                targetCredential,
                ct,
                oauthApplicationId: crossTenantAppId,
                partnerTenantId: source.TenantId.ToString());
            tgtOrgStatus = created ? "created" : "existing";
        }
        catch (InvalidOperationException ex)
        {
            tgtOrgError = ex.Message;
            _logger.LogWarning(ex, "Exchange setup: failed to ensure target org relationship for project {Id}.", id);
        }

        // Step 3 — Target tenant: cross-tenant migration endpoint.
        // Per Microsoft cross-tenant mailbox migration docs, the migration endpoint is created
        // in the TARGET tenant and points back at the SOURCE tenant domain. Using the source
        // tenant here is incorrect and causes EXO to return 400 or create an unusable endpoint.
        // ApplicationId must match the OAuthApplicationId stamped on both org relationships.
        try
        {
            var (identity, wasCreated) = await _exoClient.EnsureMigrationEndpointAsync(
                target.TenantId.ToString(),
                sourceDomain,
                targetCredential,
                ct,
                applicationId: crossTenantAppId);
            endpointIdentity = identity;
            epStatus = wasCreated ? "created" : "existing";
        }
        catch (InvalidOperationException ex)
        {
            epError = ex.Message;
            _logger.LogWarning(ex, "Exchange setup: failed to ensure migration endpoint for project {Id}.", id);
        }

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "PROJECT_EXCHANGE_SETUP",
            Resource  = $"projects/{id}",
            Actor     = _currentUser.UserName,
            ProjectId = id,
            Details   = $$$"""{"srcOrgStatus":"{{{srcOrgStatus}}}","tgtOrgStatus":"{{{tgtOrgStatus}}}","epStatus":"{{{epStatus}}}","endpointIdentity":"{{{endpointIdentity}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation(
            "Exchange setup for project {Id}: sourceOrgRel={SrcStatus}, targetOrgRel={TgtStatus}, endpoint={EpStatus} ({EpIdentity}).",
            id, srcOrgStatus, tgtOrgStatus, epStatus, endpointIdentity);

        var warnings = new List<string>();
        if (srcOrgError is not null) warnings.Add($"Source org relationship: {srcOrgError}");
        if (tgtOrgError is not null) warnings.Add($"Target org relationship: {tgtOrgError}");
        if (epError is not null)     warnings.Add($"Migration endpoint: {epError}");

        return Ok(new
        {
            sourceOrgRelationship = new { status = srcOrgStatus, domain = targetDomain, error = srcOrgError },
            targetOrgRelationship = new { status = tgtOrgStatus, domain = sourceDomain, error = tgtOrgError },
            migrationEndpoint     = new { status = epStatus, identity = endpointIdentity, error = epError },
            warnings,
        });
    }

    /// <summary>
    /// Returns a structured checklist of migration prerequisites for the project.
    /// Each check is one of: pass / fail / warning / skipped.
    /// The overall status is "blocked" (any fail), "warning" (any warning, no fail), or "ready".
    /// </summary>
    [HttpGet("{id:guid}/dependency-check")]
    public async Task<IActionResult> DependencyCheck(Guid id, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(id, ct);
        if (project is null) return NotFound();

        var source = project.SourceTenant!;
        var target = project.TargetTenant!;
        var checks = new List<DependencyCheck>();

        // ── Tenant checks ─────────────────────────────────────────────────────
        foreach (var (tenant, label) in new[] { (source, "Source"), (target, "Target") })
        {
            var prefix = label.ToLowerInvariant();

            // Load credentials from Key Vault (if enabled) or fall back to DB columns,
            // then try to construct a TokenCredential. This is the single source of truth
            // for whether credentials are present and structurally valid — checking only DB
            // columns misses Key Vault-stored certificates.
            var (certB64, certPw, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, ct);
            try
            {
                _credentialFactory.CreateCredential(tenant, certB64, certPw, secret);

                // Determine what credential type is in use for the detail string.
                bool usingCert = !string.IsNullOrWhiteSpace(certB64 ?? tenant.ClientCertificateBase64);
                string credType = usingCert ? "certificate" : "client secret";
                string appIdHint = tenant.AppClientId.Length >= 8
                    ? $"{tenant.AppClientId[..8]}…"
                    : tenant.AppClientId;

                checks.Add(new DependencyCheck(
                    $"{prefix}-credentials",
                    "Tenants",
                    $"{label} tenant credentials",
                    "pass",
                    $"App ID {appIdHint} · {credType}",
                    null));
            }
            catch (InvalidOperationException ex)
            {
                checks.Add(new DependencyCheck(
                    $"{prefix}-credentials",
                    "Tenants",
                    $"{label} tenant credentials",
                    "fail",
                    ex.Message,
                    $"Open Tenants → {label} tenant → Re-configure App. Ensure the App Client ID and credentials match the current app registration."));
            }

            // Connection verified
            checks.Add(new DependencyCheck(
                $"{prefix}-connected",
                "Tenants",
                $"{label} tenant connection verified",
                tenant.ConnectionStatus == ConnectionStatus.Connected ? "pass" : "fail",
                tenant.ConnectionStatus == ConnectionStatus.Connected
                    ? $"Last verified {(tenant.LastVerifiedAt.HasValue ? tenant.LastVerifiedAt.Value.ToString("u") : "recently")}"
                    : $"Current status: {tenant.ConnectionStatus}",
                tenant.ConnectionStatus == ConnectionStatus.Connected
                    ? null
                    : $"Open Tenants → {label} tenant → Verify Connection."));
        }

        // Target OnMicrosoftDomain (required to build the migration endpoint URL)
        checks.Add(new DependencyCheck(
            "target-onmicrosoft",
            "Tenants",
            "Target .onmicrosoft.com domain detected",
            string.IsNullOrWhiteSpace(target.OnMicrosoftDomain) ? "fail" : "pass",
            string.IsNullOrWhiteSpace(target.OnMicrosoftDomain)
                ? "Not detected — required for cross-tenant migration endpoint."
                : $"{target.OnMicrosoftDomain}.onmicrosoft.com",
            string.IsNullOrWhiteSpace(target.OnMicrosoftDomain)
                ? "Open Tenants → Target tenant → Verify Connection. The domain is auto-detected on successful verification."
                : null));

        // ── Discovery checks ──────────────────────────────────────────────────
        var allScans    = (await _scans.GetAllAsync(id, ct)).ToList();
        var latestScan  = allScans.FirstOrDefault(s => s.Status == ScanStatus.Completed);

        checks.Add(new DependencyCheck(
            "scan-completed",
            "Discovery",
            "Discovery scan completed",
            latestScan is null ? "fail" : "pass",
            latestScan is null
                ? $"{allScans.Count} scan(s) found — none completed yet."
                : $"Scan {latestScan.Id.ToString()[..8]}… completed {latestScan.CompletedAt:u}",
            latestScan is null
                ? "Go to the Scans tab and run a Full scan."
                : null));

        if (latestScan is not null)
        {
            var blockers = latestScan.Summary?.BlockerCount ?? 0;
            checks.Add(new DependencyCheck(
                "scan-blockers",
                "Discovery",
                "No scan blockers",
                blockers == 0 ? "pass" : "fail",
                blockers == 0
                    ? $"Readiness score: {latestScan.Summary?.ReadinessScore ?? 0}/100"
                    : $"{blockers} blocker(s) detected — score {latestScan.Summary?.ReadinessScore ?? 0}/100",
                blockers == 0
                    ? null
                    : "Review blockers on the Overview tab and resolve each issue before migrating."));
        }
        else
        {
            checks.Add(new DependencyCheck(
                "scan-blockers",
                "Discovery",
                "No scan blockers",
                "skipped",
                "No completed scan to evaluate.",
                null));
        }

        // ── Identity mapping ──────────────────────────────────────────────────
        var maps      = (await _identityMaps.GetByProjectAsync(id, ct)).ToList();
        var mapped    = maps.Count(m => m.Status == MappingStatus.Mapped);
        var unmapped  = maps.Count(m => m.Status == MappingStatus.Unmapped);

        checks.Add(new DependencyCheck(
            "identity-mapped",
            "Identity",
            "Identity mapping configured",
            maps.Count == 0 ? "warning" : "pass",
            maps.Count == 0
                ? "No identity maps found."
                : $"{mapped} mapped, {unmapped} unmapped of {maps.Count} total",
            maps.Count == 0
                ? "Go to the Identity Mapping tab and run Auto-Map, or import a CSV."
                : (unmapped > 0 ? $"{unmapped} user(s) have no target UPN — they will be skipped during migration." : null)));

        // ── Exchange setup ────────────────────────────────────────────────────
        // Cannot be checked without live EXO calls; remind the user to run Setup Exchange.
        checks.Add(new DependencyCheck(
            "exchange-setup",
            "Exchange",
            "Exchange org relationships & migration endpoint",
            "warning",
            "Cannot be verified automatically without querying Exchange Online.",
            "Go to the Overview tab → Exchange Online Prerequisites → Setup Exchange Migration. It is idempotent — safe to run again."));

        // ── SPO cross-tenant relationship ─────────────────────────────────────
        // Set-SPOCrossTenantRelationship must be run on both tenants before the
        // /_api/CrossTenantMigration/ REST surface is available.  Without it every
        // content migration start returns 404 "Cannot find resource CrossTenantMigration".
        var sourceOnMs = source.OnMicrosoftDomain;
        var targetOnMs = target.OnMicrosoftDomain;
        var spoSetupDetail = string.IsNullOrWhiteSpace(sourceOnMs) || string.IsNullOrWhiteSpace(targetOnMs)
            ? "Cannot build SPO admin URLs — re-verify both tenants first."
            : $"Source admin: https://{sourceOnMs}-admin.sharepoint.com  |  Target admin: https://{targetOnMs}-admin.sharepoint.com";
        checks.Add(new DependencyCheck(
            "spo-cross-tenant-relationship",
            "SharePoint",
            "SPO cross-tenant relationship configured",
            "warning",
            spoSetupDetail,
            "The platform establishes this automatically on the first content-migration start " +
            "(ContentMigration:AutoEstablishRelationship, default on). Manual fallback in SharePoint Online Management Shell — " +
            $"target admin (Connect-SPOService -Url https://{targetOnMs ?? "<target-onmicrosoft>"}-admin.sharepoint.com):\n" +
            $"  Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Source -PartnerCrossTenantHostUrl https://{sourceOnMs ?? "<source-onmicrosoft>"}-my.sharepoint.com\n" +
            $"then source admin (Connect-SPOService -Url https://{sourceOnMs ?? "<source-onmicrosoft>"}-admin.sharepoint.com):\n" +
            $"  Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Target -PartnerCrossTenantHostUrl https://{targetOnMs ?? "<target-onmicrosoft>"}-my.sharepoint.com\n" +
            "then verify both sides with Test-SPOCrossTenantRelationship (expect GoodToProceed)."));

        // ── Cross-tenant sync (only when at least one user batch opts into it) ─
        var userBatches = (await _userMigrations.GetBatchesByProjectAsync(id, ct)).ToList();
        var ctsBatches = userBatches
            .Where(b => b.Strategy == UserMigrationStrategy.CrossTenantSync)
            .ToList();
        if (ctsBatches.Count > 0)
        {
            // Probe the target tenant for an existing cross-tenant sync configuration.
            // Discovery errors fall back to a "warning" with the underlying message
            // rather than failing the whole dependency check.
            CrossTenantSyncDiscoveryResult discovery;
            try
            {
                discovery = await _ctsDiscovery.DiscoverAsync(source, target, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Dependency check: cross-tenant sync discovery threw for project {Id}.", id);
                discovery = new CrossTenantSyncDiscoveryResult(
                    IsConfigured: false,
                    PartnerPolicyConfigured: false,
                    ServicePrincipalId: null,
                    ServicePrincipalDisplayName: null,
                    SyncJobId: null,
                    SyncJobTemplateId: null,
                    SyncJobStatus: null,
                    LastSyncAt: null,
                    Message: $"Discovery failed: {ex.Message}",
                    Remediation: "Verify the target tenant credentials and Application.Read.All / Synchronization.Read.All permissions.",
                    Error: ex.Message);
            }

            var ctsStatus = discovery.Error is not null
                ? "warning"
                : discovery.IsConfigured ? "pass" : "fail";

            checks.Add(new DependencyCheck(
                "cross-tenant-sync-app",
                "Identity",
                "Entra cross-tenant sync app & job configured in target tenant",
                ctsStatus,
                $"{ctsBatches.Count} user batch(es) selected CrossTenantSync. {discovery.Message}",
                discovery.Remediation));
        }

        // ── Wave planning ─────────────────────────────────────────────────────
        var waves = (await _waves.GetWavesByProjectAsync(id, ct)).ToList();
        checks.Add(new DependencyCheck(
            "waves-configured",
            "Waves",
            "Migration waves configured",
            waves.Count == 0 ? "warning" : "pass",
            waves.Count == 0 ? "No waves defined." : $"{waves.Count} wave(s) defined",
            waves.Count == 0
                ? "Go to the Waves tab and create at least one wave, then assign mailbox batches or content jobs to it."
                : null));

        // ── Overall status ────────────────────────────────────────────────────
        string overall = checks.Any(c => c.Status == "fail")    ? "blocked"
                       : checks.Any(c => c.Status == "warning") ? "warning"
                       : "ready";

        return Ok(new DependencyCheckResult(overall, checks));
    }

    /// <summary>
    /// Probe the target tenant for an Entra cross-tenant synchronization
    /// configuration that targets the source tenant. Drives the discovery card
    /// on the project overview page and the inline status indicator in the
    /// Create User Batch dialog when CrossTenantSync is selected.
    /// </summary>
    [HttpGet("{id:guid}/cross-tenant-sync-status")]
    public async Task<IActionResult> CrossTenantSyncStatus(Guid id, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(id, ct);
        if (project is null) return NotFound();
        if (project.SourceTenant is null || project.TargetTenant is null)
            return UnprocessableEntity(new { message = "Project source or target tenant not loaded." });

        var result = await _ctsDiscovery.DiscoverAsync(project.SourceTenant, project.TargetTenant, ct);
        return Ok(result);
    }

    /// <summary>
    /// Set how migrated identities relate to the target tenant's directory:
    /// cloudOnly (default) or hybrid (Entra Connect target — enables the on-prem
    /// AD handoff kit and the directory-sync validation check).
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{id:guid}/target-directory-mode")]
    public async Task<IActionResult> UpdateTargetDirectoryMode(
        Guid id, [FromBody] UpdateTargetDirectoryModeRequest req, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(id, ct);
        if (project is null) return NotFound();

        var previous = project.TargetDirectoryMode;
        project.TargetDirectoryMode = req.Mode;
        await _projects.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action    = "PROJECT_TARGET_DIRECTORY_MODE_CHANGED",
            Resource  = $"projects/{id}",
            Actor     = _currentUser.UserName,
            ProjectId = id,
            Details   = $$$"""{"previous":"{{{previous}}}","mode":"{{{req.Mode}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return Ok(project);
    }

    [Authorize(Policy = "Operator")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await _projects.ExistsAsync(id, ct)) return NotFound();

        await _projects.DeleteAsync(id, ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "PROJECT_DELETED",
            Resource = $"projects/{id}",
            Actor = _currentUser.UserName,
            ProjectId = null,
        }, ct);
        await _audit.SaveAsync(ct);

        return NoContent();
    }
}
