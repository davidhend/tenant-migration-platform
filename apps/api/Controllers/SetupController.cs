using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Computes the guided pre-setup plan for a project's tenant pair: admin-consent
/// URLs, filled-in per-tenant bootstrap scripts (the two things an app cannot do
/// to itself — EXO service-principal registration and the Exchange Administrator
/// directory role), and appsettings completeness. Read-only; live verification is
/// delegated to the existing diagnostics tenant-prereqs endpoint so checks are
/// never duplicated here.
/// </summary>
[ApiController]
[Route("api/setup")]
[Authorize]
public class SetupController : ControllerBase
{
    // Well-known Entra directory role template ID for Exchange Administrator.
    private const string ExchangeAdministratorRoleId = "29232cdf-9323-42fd-ade2-1d097af3e4de";

    private readonly IProjectRepository _projects;
    private readonly IConfiguration _config;
    private readonly IPlatformSecretResolver _secrets;
    private readonly ILogger<SetupController> _logger;

    public SetupController(
        IProjectRepository projects,
        IConfiguration config,
        IPlatformSecretResolver secrets,
        ILogger<SetupController> logger)
    {
        _projects = projects;
        _config = config;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Build the pre-setup plan for the project's tenant pair.</summary>
    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> GetPlan(Guid projectId, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
        if (project is null)
            return NotFound(new { message = $"Project {projectId} not found." });
        if (project.SourceTenant is null || project.TargetTenant is null)
            return UnprocessableEntity(new { message = "Project is missing a source or target tenant." });

        var source = project.SourceTenant;
        var target = project.TargetTenant;

        var migrationAppId = _config["Platform:CrossTenantMigration:AppId"];
        var secretConfigured = !string.IsNullOrWhiteSpace(
            await _secrets.GetAsync("Platform:CrossTenantMigration:ClientSecret", ct));

        var plan = new SetupPlanResponse
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            SourceTenant = ToTenantInfo(source),
            TargetTenant = ToTenantInfo(target),
            MigrationAppId = string.IsNullOrWhiteSpace(migrationAppId) ? null : migrationAppId,
            ClientSecretConfigured = secretConfigured,
            GeneratedAt = DateTime.UtcNow,
        };

        var steps = plan.Steps;

        // ── Exchange: migration app registration + consent ───────────────────
        var appConfigured = !string.IsNullOrWhiteSpace(migrationAppId);
        steps.Add(new SetupStep
        {
            Id = "exchange.migration-app",
            Title = "Create the cross-tenant Mailbox Migration app",
            Category = "exchange",
            Audience = "targetAdmin",
            Kind = "config",
            Status = appConfigured ? "done" : "pending",
            Detail = appConfigured
                ? $"Platform:CrossTenantMigration:AppId is set ({migrationAppId})."
                : "In the TARGET tenant: Entra → App registrations → New registration. " +
                  "The TARGET tenant is a hard requirement, not a convention — a source-homed multitenant app " +
                  "with identical consents is rejected by the source-side MRS ProxyService with a bare " +
                  "'Access is denied' during the move (confirmed live 2026-07-13). " +
                  "Supported account types: 'Accounts in any organizational directory (multitenant)'. " +
                  "Redirect URI: type Web, value https://office.com (exact URL required). " +
                  "Then API permissions → Office 365 Exchange Online → Application permissions → Mailbox.Migration → Grant admin consent. " +
                  "Copy the Application (client) ID from the Overview blade (NOT the enterprise-app object ID) and " +
                  "save it in Settings → Pre-Setup → Cross-Tenant Mailbox Migration App (applied without restart), " +
                  "or in appsettings.json Platform:CrossTenantMigration:AppId.",
        });

        // Tenants with Entra's secure-by-default app management policy block secret
        // creation ("Credential type not allowed as per assigned policy") — confirmed
        // live 2026-07-13. The fix is a scoped appManagementPolicy exemption assigned
        // to just this app; the script below is the runnable form (az CLI, works from
        // PowerShell or bash; requires a Global Administrator az login to the target
        // tenant). A secret is unavoidable — the migration endpoint only supports
        // PSCredential auth, not certificates.
        {
            var appIdForPolicy = string.IsNullOrWhiteSpace(migrationAppId)
                ? "<migration-app-client-id>" : migrationAppId;
            var policyExemptionScript = $$$"""
                # ONLY needed if 'New client secret' fails with
                # "Credential type not allowed as per assigned policy".
                # Run as a Global Administrator of the TARGET tenant (az login to that tenant).

                $appObjectId = az ad app show --id {{{appIdForPolicy}}} --query id -o tsv

                $policyId = az rest --method POST `
                  --url https://graph.microsoft.com/v1.0/policies/appManagementPolicies `
                  --headers "Content-Type=application/json" `
                  --body '{"displayName":"Allow client secret - cross-tenant mailbox migration app","isEnabled":true,"restrictions":{"passwordCredentials":[{"restrictionType":"passwordAddition","state":"disabled"}]}}' `
                  --query id -o tsv

                az rest --method POST `
                  --url "https://graph.microsoft.com/v1.0/applications/$appObjectId/appManagementPolicies/`$ref" `
                  --headers "Content-Type=application/json" `
                  --body "{\"@odata.id\":\"https://graph.microsoft.com/v1.0/policies/appManagementPolicies/$policyId\"}"

                # Then retry creating the client secret on the migration app.
                """;

            steps.Add(new SetupStep
            {
                Id = "exchange.migration-app-secret",
                Title = "Create a client secret on the migration app",
                Category = "exchange",
                Audience = "targetAdmin",
                Kind = "config",
                Status = secretConfigured ? "done" : "pending",
                Detail = secretConfigured
                    ? "Platform:CrossTenantMigration:ClientSecret is set. The platform creates the migration endpoint automatically."
                    : "On the migration app (in its home tenant): Certificates & secrets → New client secret. " +
                      "Save the secret VALUE (not the secret ID) in Settings → Pre-Setup → Cross-Tenant Mailbox Migration App " +
                      "(applied without restart), or in appsettings.json Platform:CrossTenantMigration:ClientSecret. " +
                      "Note the expiry date — endpoint authentication fails silently when it lapses. " +
                      "If secret creation fails with 'Credential type not allowed as per assigned policy', the tenant " +
                      "has Entra's secure-by-default app management policy — run the script below to create a scoped " +
                      "exemption for this app, then retry (a secret is unavoidable — the migration endpoint does not " +
                      "support certificate auth).",
                Script = secretConfigured ? null : policyExemptionScript,
            });
        }

        AddConsentStep(steps, "source", source, migrationAppId);
        AddConsentStep(steps, "target", target, migrationAppId);

        // ── Exchange: manual cross-tenant migration endpoint ─────────────────
        // Confirmed by live validation: New-MigrationEndpoint CANNOT be created
        // over the EXO REST InvokeCommand surface — the -Credentials PSCredential
        // is a client-side PowerShell type that doesn't serialize (EXO returns a
        // 500 "Unable to cast JObject to String"). So this is a one-time MANUAL
        // step. It must use the CURRENT client secret VALUE: a stale secret makes
        // the mailbox move fail deep in MRS with AADSTS7000215 (Invalid client
        // secret), long after setup appears to succeed.
        {
            var appId = string.IsNullOrWhiteSpace(migrationAppId) ? "<migration-app-client-id>" : migrationAppId;
            var sourceDomain = FullOnMicrosoftDomain(source) ?? "<source>.onmicrosoft.com";
            var targetPrefix = OnMicrosoftPrefix(target);
            var endpointScript = $$"""
                # Run in TARGET tenant Exchange Online PowerShell (Connect-ExchangeOnline).
                # One-time per tenant pair. The platform reuses this endpoint but cannot
                # create or refresh it (PSCredential can't be passed over EXO REST).
                # Use the client secret VALUE (not the secret ID) from the migration app.

                $appId  = '{{appId}}'
                $secret = ConvertTo-SecureString '<CLIENT-SECRET-VALUE>' -AsPlainText -Force
                $cred   = New-Object System.Management.Automation.PSCredential($appId, $secret)

                New-MigrationEndpoint -Name CrossTenantEndpoint -ExchangeRemoteMove `
                    -RemoteTenant {{sourceDomain}} -RemoteServer outlook.office.com `
                    -ApplicationId $appId -Credentials $cred

                # If a later mailbox move fails with AADSTS7000215 (Invalid client
                # secret), this endpoint's stored secret is stale — recreate it:
                #   Remove-MigrationEndpoint -Identity CrossTenantEndpoint
                # then re-run the New-MigrationEndpoint above with the current secret.
                """;
            steps.Add(new SetupStep
            {
                Id = "exchange.migration-endpoint",
                Title = $"Create the cross-tenant migration endpoint in {target.DisplayName} (target)",
                Category = "exchange",
                Audience = "targetAdmin",
                Kind = "script",
                Status = "unknown",
                Detail = "The migration endpoint carries the app credential MRS uses to pull mailboxes across tenants. " +
                         "It must be created manually in target-tenant EXO PowerShell — it cannot be created over the " +
                         "REST API the platform uses (the -Credentials PSCredential doesn't serialize). Use the CURRENT " +
                         "client secret VALUE; a stale secret surfaces only later as AADSTS7000215 during the move. " +
                         $"Uses the migration app {appId} and remote tenant {sourceDomain}. Verify with Run checks (endpoint.*).",
                Script = endpointScript,
            });
        }

        // ── Entra/EXO: per-tenant bootstrap scripts ──────────────────────────
        AddBootstrapScriptStep(steps, "source", source);
        AddBootstrapScriptStep(steps, "target", target);

        // ── Config: per-tenant credential presence ───────────────────────────
        AddCredentialStep(steps, "source", source);
        AddCredentialStep(steps, "target", target);

        // ── Azure: Automation account config (SPO/OneDrive path) ────────────
        var automation = _config.GetSection("Azure:Automation");
        string[] automationKeys = ["SubscriptionId", "ResourceGroup", "AccountName", "RunbookName"];
        var missing = automationKeys.Where(k => string.IsNullOrWhiteSpace(automation[k])).ToList();
        steps.Add(new SetupStep
        {
            Id = "azure.automation-config",
            Title = "Azure Automation account configured (SharePoint/OneDrive migrations)",
            Category = "azure",
            Audience = "either",
            Kind = "config",
            Status = missing.Count == 0 ? "done" : "pending",
            Detail = missing.Count == 0
                ? "Azure:Automation settings are filled in. Grant the API identity the Automation Contributor role on the " +
                  "Automation account — the API then publishes and updates the runbook itself at startup (with only " +
                  "Job Operator, jobs still run but the runbook must be re-imported manually after changes)."
                : $"appsettings.json Azure:Automation is missing: {string.Join(", ", missing)}. " +
                  "Create an Automation account, import the Microsoft.Online.SharePoint.PowerShell module, and grant the " +
                  "API identity Automation Contributor (the API auto-publishes the runbook at startup).",
        });

        // Graph application permissions the platform features need beyond the
        // baseline read scopes — consent is per-tenant and admin-interactive.
        foreach (var (tenant, side) in new[] { (plan.SourceTenant, "source"), (plan.TargetTenant, "target") })
        {
            steps.Add(new SetupStep
            {
                Id = $"entra.graph-permissions-{side}",
                Title = $"Grant Graph application permissions on the platform app in {tenant.DisplayName} ({side})",
                Category = "entra",
                Audience = side == "source" ? "sourceAdmin" : "targetAdmin",
                Kind = "info",
                Status = "unknown",
                Detail = $"On app registration {tenant.AppClientId ?? "<platform app>"}: " +
                         "User.ReadWrite.All + Organization.Read.All (auto-assigning Cross Tenant User Data Migration licenses" +
                         (side == "target" ? " — default assignment side" : "") + "), " +
                         (side == "source"
                             ? "Synchronization.ReadWrite.All + Application.Read.All (cross-tenant sync discovery), "
                             : "Policy.Read.All (inbound partner-policy check), ") +
                         "then Grant admin consent. Use Run checks below to verify.",
            });
        }

        // ── SharePoint application permissions (content migration) ───────────
        // Confirmed by live validation: cross-tenant OneDrive/SharePoint content
        // operations authenticate to SPO app-only and fail "Access is denied /
        // unauthorized" without these. Target side also provisions OneDrive, so it
        // gets OneDrive.Provision.All. Automated by the Terraform tenant stacks.
        foreach (var (tenant, side) in new[] { (plan.SourceTenant, "source"), (plan.TargetTenant, "target") })
        {
            var roles = side == "target"
                ? "Sites.FullControl.All, User.ReadWrite.All, OneDrive.Provision.All, SharePointCrossTenantMigration.Manage.All, Migration.ReadWrite.All"
                : "Sites.FullControl.All, User.ReadWrite.All, SharePointCrossTenantMigration.Manage.All, Migration.ReadWrite.All";
            steps.Add(new SetupStep
            {
                Id = $"spo.permissions-{side}",
                Title = $"Grant SharePoint application permissions on the platform app in {tenant.DisplayName} ({side})",
                Category = "spo",
                Audience = side == "source" ? "sourceAdmin" : "targetAdmin",
                Kind = "info",
                Status = "unknown",
                Detail = $"On app registration {tenant.AppClientId ?? "<platform app>"}: API permissions → " +
                         "'APIs my organization uses' → Office 365 SharePoint Online → Application permissions → " +
                         $"add {roles} → Grant admin consent. Required for OneDrive/SharePoint content migration " +
                         "(missing them fails with 'Access is denied' during the move). The Terraform " +
                         $"{side}-tenant stack automates this (platform_sharepoint_roles).",
            });
        }

        // ── Interactive OneDrive pre-provisioning (the app-only gotcha) ──────
        // Confirmed live: Request-SPOPersonalSite does NOT work with app-only /
        // certificate auth ("unauthorized operation" even with every SharePoint
        // permission granted) — the User Profile Service needs an interactive admin
        // context. So a SharePoint admin must run it by hand once per batch of
        // target users, before starting a OneDrive content migration.
        {
            var targetPrefix = OnMicrosoftPrefix(target);
            var targetFull = FullOnMicrosoftDomain(target) ?? "target.onmicrosoft.com";
            var exampleUpn1 = $"user1@{targetFull}";
            var exampleSiteSegment = exampleUpn1.Replace('.', '_').Replace('@', '_');
            var clearSiteScript = $$"""
                # ONLY needed if a migrating user ALREADY HAS a OneDrive on the target
                # tenant (e.g. from a prior migration attempt or pre-provisioning).
                # Run as a SharePoint Administrator. The cross-tenant move CREATES the
                # target personal site itself and fails with "The target tenant has a
                # conflict for the site provided" when one exists. Both commands are
                # required — a soft-deleted site in the recycle bin still conflicts.

                Connect-SPOService -Url https://{{targetPrefix}}-admin.sharepoint.com

                # Repeat per conflicting user (URL pattern: dots/@ become underscores):
                Remove-SPOSite        -Identity https://{{targetPrefix}}-my.sharepoint.com/personal/{{exampleSiteSegment}} -Confirm:$false
                Remove-SPODeletedSite -Identity https://{{targetPrefix}}-my.sharepoint.com/personal/{{exampleSiteSegment}} -Confirm:$false
                """;
            steps.Add(new SetupStep
            {
                Id = "spo.onedrive-target-must-not-exist",
                Title = $"Ensure target OneDrive sites do NOT exist in {target.DisplayName}",
                Category = "spo",
                Audience = "targetAdmin",
                Kind = "script",
                Status = "unknown",
                Detail = "The cross-tenant OneDrive move CREATES each user's target personal site itself — do NOT " +
                         "pre-provision target OneDrives (Request-SPOPersonalSite), and remove any that already " +
                         "exist before starting a OneDrive job (confirmed live: an existing target site fails the " +
                         "move with a site conflict). The platform's start preflight checks this and lists the " +
                         "conflicting users with this same removal guidance. Target users still need their " +
                         "licenses; they just must not have a OneDrive yet.",
                Script = clearSiteScript,
            });
        }

        // ── SharePoint SITE cross-tenant feature (MnASiteMove) ───────────────
        // Confirmed live: a SharePoint site move reaches the StartSite runbook op
        // and Microsoft rejects it unless this tenant-level feature is enabled. It
        // is SEPARATE from the OneDrive cross-tenant move (which is enabled by
        // default) — only site migrations need it.
        {
            var srcName = plan.SourceTenant.DisplayName;
            var tgtName = plan.TargetTenant.DisplayName;
            var srcDomain = FullOnMicrosoftDomain(source) ?? "<source>.onmicrosoft.com";
            var tgtDomain = FullOnMicrosoftDomain(target) ?? "<target>.onmicrosoft.com";
            var srcId = plan.SourceTenant.AadTenantId ?? "<source-tenant-id>";
            var tgtId = plan.TargetTenant.AadTenantId ?? "<target-tenant-id>";
            var enablementRequest = $"""
                Subject: Request to enable MnASiteMove (cross-tenant SharePoint site migration) for our tenants

                Hello,

                We are performing a cross-tenant migration and need the tenant-level
                MnASiteMove feature (cross-tenant SharePoint SITE migration, per
                https://learn.microsoft.com/en-us/sharepoint/cross-tenant-sharepoint-migration)
                enabled on BOTH of the following tenants:

                  Source: {srcName} ({srcDomain}, tenant ID {srcId})
                  Target: {tgtName} ({tgtDomain}, tenant ID {tgtId})

                Currently, Start-SPOCrossTenantSiteContentMove fails with:
                "The Cross-Tenant content move [MnASiteMove] feature is not enabled for this tenant."

                The cross-tenant MnA relationship between the tenants is established and
                verified (Test-SPOCrossTenantRelationship reports GoodToProceed), and the
                cross-tenant identity map is uploaded on the target tenant.

                Please enable MnASiteMove on both tenants, or advise on the correct
                onboarding process if a different request channel is required.

                Thank you.
                """;
            steps.Add(new SetupStep
            {
                Id = "spo.mnasitemove-feature",
                Title = "Enable cross-tenant SharePoint site migration (MnASiteMove)",
                Category = "spo",
                Audience = "either",
                Kind = "script",
                Status = "unknown",
                Detail = "ONLY needed for SharePoint SITE migrations (not OneDrive, not mailbox). Cross-tenant SharePoint " +
                         "site moves require the tenant-level MnASiteMove feature, which Microsoft must enable — file a " +
                         "Microsoft support request (Microsoft 365 admin center → Support, from either tenant) using the " +
                         "example below. It is separate from the OneDrive cross-tenant move (enabled by default) and from " +
                         "mailbox migration. Symptom if missing: starting a SharePoint site job fails with 'The Cross-Tenant " +
                         "content move [MnASiteMove] feature is not enabled for this tenant.' Skip this step if you are not " +
                         "migrating SharePoint sites.",
                ActionUrl = "https://learn.microsoft.com/en-us/sharepoint/cross-tenant-sharepoint-migration",
                Script = enablementRequest,
            });
        }

        // ── Cross-tenant access settings (manual by choice — the azuread
        // Terraform provider has no crossTenantAccessPolicy support, and the
        // user opted to keep this step manual rather than adopt the msgraph
        // provider). Required only for the CrossTenantSync user-migration
        // strategy; mailbox MRS and SPO/OneDrive migrations do not use it.
        steps.Add(new SetupStep
        {
            Id = "entra.cross-tenant-access-target",
            Title = $"Configure cross-tenant access settings in {plan.TargetTenant.DisplayName} (target)",
            Category = "entra",
            Audience = "targetAdmin",
            Kind = "info",
            Status = "unknown",
            Detail = "Needed for Entra cross-tenant sync (user migration strategy CrossTenantSync). " +
                     "Entra admin center → External Identities → Cross-tenant access settings → Organizational settings → " +
                     $"Add organization → enter the SOURCE tenant ID {plan.SourceTenant.AadTenantId}. Then on that organization: " +
                     "Inbound access → Cross-tenant sync tab → check 'Allow users sync into this tenant'; " +
                     "Inbound access → Trust settings → enable 'Automatically redeem invitations with tenant'. " +
                     "The dependency check's cross-tenant-sync probe verifies the partner policy exists.",
        });
        steps.Add(new SetupStep
        {
            Id = "entra.cross-tenant-access-source",
            Title = $"Configure cross-tenant access settings in {plan.SourceTenant.DisplayName} (source)",
            Category = "entra",
            Audience = "sourceAdmin",
            Kind = "info",
            Status = "unknown",
            Detail = "Needed for Entra cross-tenant sync (user migration strategy CrossTenantSync). " +
                     "Entra admin center → External Identities → Cross-tenant access settings → Organizational settings → " +
                     $"Add organization → enter the TARGET tenant ID {plan.TargetTenant.AadTenantId}. Then on that organization: " +
                     "Outbound access → Trust settings → enable 'Automatically redeem invitations with tenant'. " +
                     "The cross-tenant sync configuration itself (Entra → Cross-tenant synchronization → Configurations) " +
                     "also lives in this tenant and is discovered automatically by the platform once created.",
        });

        // ── Irreducibles ─────────────────────────────────────────────────────
        steps.Add(new SetupStep
        {
            Id = "info.license",
            Title = "Purchase Cross Tenant User Data Migration licenses",
            Category = "exchange",
            Audience = "either",
            Kind = "info",
            Status = "unknown",
            Detail = "One license per migrating user (one-time fee; covers mailbox AND OneDrive migration; assignable on source OR target). " +
                     "M365 admin center → Billing → Purchase services → search 'Cross Tenant User Data Migration'. " +
                     "Only PURCHASING seats is manual — the platform auto-assigns the license to batch members at " +
                     "migration start (MailboxMigration:AutoAssignLicense / ContentMigration:AutoAssignLicense, both default on). " +
                     "Without seats EXO reports 'needs approval' and the move stalls.",
        });
        steps.Add(new SetupStep
        {
            Id = "info.domains",
            Title = "Verify custom domains",
            Category = "entra",
            Audience = "either",
            Kind = "info",
            Status = "unknown",
            Detail = "Any custom domain used by source UPNs or target routing must be verified in its tenant " +
                     "(Entra → Custom domain names) and present as an accepted domain in Exchange Online.",
        });

        _logger.LogInformation(
            "Setup plan generated for project {ProjectId}: {StepCount} steps, {PendingCount} pending.",
            project.Id, steps.Count, steps.Count(s => s.Status == "pending"));

        return Ok(plan);
    }

    private static SetupTenantInfo ToTenantInfo(Tenant t) => new()
    {
        Id = t.Id,
        DisplayName = t.DisplayName,
        AadTenantId = t.TenantId,
        OnMicrosoftDomain = t.OnMicrosoftDomain,
        AppClientId = t.AppClientId,
        CredentialConfigured =
            !string.IsNullOrWhiteSpace(t.ClientCertificateBase64) ||
            !string.IsNullOrWhiteSpace(t.ClientCertificateThumbprint) ||
            !string.IsNullOrWhiteSpace(t.ClientSecretHint),
        VerifyEndpoint = $"/diagnostics/tenant-prereqs/{t.Id}",
    };

    private static void AddConsentStep(List<SetupStep> steps, string side, Tenant tenant, string? migrationAppId)
    {
        var domain = FullOnMicrosoftDomain(tenant);
        var hasApp = !string.IsNullOrWhiteSpace(migrationAppId);
        steps.Add(new SetupStep
        {
            Id = $"exchange.consent-{side}",
            Title = $"Admin-consent the migration app in {tenant.DisplayName} ({side})",
            Category = "exchange",
            Audience = side == "source" ? "sourceAdmin" : "targetAdmin",
            Kind = "link",
            // Consent state isn't cheaply verifiable without a Graph call; the
            // diagnostics run confirms it end-to-end via the org-relationship checks.
            Status = hasApp ? "unknown" : "pending",
            Detail = hasApp
                ? $"Open the link as a Global Administrator of {domain ?? tenant.DisplayName} and accept. " +
                  "Safe to repeat — re-consenting is idempotent."
                : "Blocked: create the migration app first (step above) so the consent URL can be generated.",
            ActionUrl = hasApp && domain is not null
                ? $"https://login.microsoftonline.com/{domain}/adminconsent?client_id={migrationAppId}&redirect_uri=https://office.com"
                : null,
        });
    }

    private static void AddBootstrapScriptStep(List<SetupStep> steps, string side, Tenant tenant)
    {
        var domain = FullOnMicrosoftDomain(tenant) ?? "<tenant>.onmicrosoft.com";
        var script = $$"""
            # One-time EXO + Entra bootstrap for {{tenant.DisplayName}} ({{domain}})
            # Run as a Global Administrator. Requires the ExchangeOnlineManagement and
            # Microsoft.Graph PowerShell modules (PowerShell 7+ recommended).

            Connect-MgGraph -TenantId {{domain}} -Scopes "Application.Read.All","RoleManagement.ReadWrite.Directory"
            $sp = Get-MgServicePrincipal -Filter "appId eq '{{tenant.AppClientId}}'"
            if (-not $sp) { throw "Service principal for app {{tenant.AppClientId}} not found - grant admin consent for the platform app in this tenant first." }

            # 1. Register the platform app's service principal inside Exchange Online.
            #    Without this EXO returns 401 on every call regardless of AAD consent.
            Connect-ExchangeOnline -Organization {{domain}}
            New-ServicePrincipal -AppId '{{tenant.AppClientId}}' -ObjectId $sp.Id

            # 2. Assign the Exchange Administrator directory role to the service principal.
            #    EXO derives cmdlet RBAC from Entra directory roles (the token's wids claim);
            #    Exchange RBAC role assignments via -App are inert for admin cmdlets.
            New-MgRoleManagementDirectoryRoleAssignment -PrincipalId $sp.Id `
                -RoleDefinitionId '{{ExchangeAdministratorRoleId}}' -DirectoryScopeId '/'
            """;

        steps.Add(new SetupStep
        {
            Id = $"entra.bootstrap-{side}",
            Title = $"Run the EXO bootstrap script in {tenant.DisplayName} ({side})",
            Category = "entra",
            Audience = side == "source" ? "sourceAdmin" : "targetAdmin",
            Kind = "script",
            Status = "unknown",
            Detail = "Registers the platform app inside Exchange Online and grants it the Exchange Administrator " +
                     "directory role — the two steps an app cannot perform on itself. " +
                     "Verify afterwards with Run checks (exo.serviceprincipal / token.roles).",
            Script = script,
        });
    }

    private static void AddCredentialStep(List<SetupStep> steps, string side, Tenant tenant)
    {
        var info = ToTenantInfo(tenant);
        steps.Add(new SetupStep
        {
            Id = $"config.credential-{side}",
            Title = $"Platform credential for {tenant.DisplayName} ({side})",
            Category = "azure",
            Audience = "either",
            Kind = "config",
            Status = info.CredentialConfigured ? "done" : "unknown",
            Detail = info.CredentialConfigured
                ? "A certificate or client secret is present on the tenant record."
                : "No certificate/secret detected on the tenant record. If the PFX lives in Key Vault this may still work — " +
                  "confirm with Run checks (credential.build), or open Tenants → Re-configure App.",
        });
    }

    // The onmicrosoft prefix (e.g. "contoso" from "contoso.onmicrosoft.com"),
    // used to build the SPO admin URL https://{prefix}-admin.sharepoint.com.
    private static string OnMicrosoftPrefix(Tenant t) =>
        string.IsNullOrWhiteSpace(t.OnMicrosoftDomain) ? "<tenant>" : t.OnMicrosoftDomain.Split('.')[0];

    private static string? FullOnMicrosoftDomain(Tenant t) =>
        string.IsNullOrWhiteSpace(t.OnMicrosoftDomain)
            ? null
            : t.OnMicrosoftDomain.Contains('.')
                ? t.OnMicrosoftDomain
                : $"{t.OnMicrosoftDomain}.onmicrosoft.com";
}
