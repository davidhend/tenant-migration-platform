using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Tenant-pair prerequisite diagnostics for cross-tenant Exchange Online mailbox
/// migration. Read-only — never mutates tenant state beyond a self-cleaning DG
/// smoke-write test that confirms whether app-only writes are blocked.
///
/// The endpoint exists because every prereq the platform depends on is opaquely
/// configured outside the platform (RBAC, EXO ServicePrincipal registration,
/// org relationships, migration endpoint, license assignment). When a batch fails
/// the underlying issue is almost always one of these — this endpoint reports
/// pass/fail/unknown with the raw EXO response for each so the operator can fix
/// it without round-tripping through PowerShell.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
[Authorize]
public class DiagnosticsController : ControllerBase
{
    private readonly ITenantRepository _tenants;
    private readonly IExoRestClient _exo;
    private readonly ITenantCredentialFactory _credFactory;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly IConfiguration _config;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        ITenantRepository tenants,
        IExoRestClient exo,
        ITenantCredentialFactory credFactory,
        IKeyVaultCredentialService keyVault,
        IConfiguration config,
        ILogger<DiagnosticsController> logger)
    {
        _tenants = tenants;
        _exo = exo;
        _credFactory = credFactory;
        _keyVault = keyVault;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Run all tenant-side prerequisite checks for cross-tenant mailbox migration
    /// against a single tenant. Pass each tenant in the pair through this endpoint.
    /// </summary>
    [HttpPost("tenant-prereqs/{tenantId:guid}")]
    public async Task<IActionResult> TenantPrereqs(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _tenants.GetByIdAsync(tenantId, ct);
        if (tenant is null)
            return NotFound(new { message = $"Tenant {tenantId} not found." });

        var checks = new List<DiagCheck>();
        var report = new TenantPrereqReport
        {
            TenantId = tenant.Id,
            DisplayName = tenant.DisplayName,
            AadTenantId = tenant.TenantId,
            AppClientId = tenant.AppClientId,
            OnMicrosoftDomain = tenant.OnMicrosoftDomain,
            CrossTenantAppId = _config["Platform:CrossTenantMigration:AppId"]
                ?? "879f1d6d-c0b7-4543-a2dd-dfa812c5179d",
            Checks = checks,
            GeneratedAt = DateTime.UtcNow,
        };

        // ── Build credential (this is the prereq for everything below) ──────
        TokenCredential cred;
        try
        {
            var (cert, pw, secret) = await _keyVault.LoadCredentialsAsync(tenant.Id, ct);
            cred = _credFactory.CreateCredential(tenant, cert, pw, secret);
        }
        catch (Exception ex)
        {
            checks.Add(DiagCheck.Fail(
                "credential.build",
                "Build TokenCredential from Key Vault + tenant model",
                $"Failed to construct credential: {ex.Message}",
                evidence: ex.ToString()));
            report.Summary = "credential.build FAIL — cannot run any further checks.";
            return Ok(report);
        }
        checks.Add(DiagCheck.Pass(
            "credential.build",
            "Build TokenCredential from Key Vault + tenant model",
            "Credential constructed successfully."));

        // ── Check 1+2: token mints, roles claim ─────────────────────────────
        string tokenClaimsString = "";
        Dictionary<string, string?> claims = new();
        try
        {
            var tokenResult = await cred.GetTokenAsync(
                new TokenRequestContext(new[] { "https://outlook.office365.com/.default" }), ct);
            claims = ParseJwtClaims(tokenResult.Token);
            tokenClaimsString = string.Join(", ", claims.Select(kv => $"{kv.Key}={kv.Value}"));

            checks.Add(DiagCheck.Pass(
                "token.mint",
                "Acquire EXO access token (https://outlook.office365.com/.default)",
                $"Token acquired. tid={claims.GetValueOrDefault("tid")} appid={claims.GetValueOrDefault("appid")} aud={claims.GetValueOrDefault("aud")}",
                evidence: tokenClaimsString));

            // Exchange.ManageAsApp surfaces in the token's "roles" claim
            var roles = claims.GetValueOrDefault("roles") ?? "";
            if (roles.Contains("Exchange.ManageAsApp", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(DiagCheck.Pass(
                    "exchange.manage_as_app",
                    "Exchange.ManageAsApp app permission consented",
                    "Token includes 'Exchange.ManageAsApp' in roles claim.",
                    evidence: $"roles={roles}"));
            }
            else
            {
                checks.Add(DiagCheck.Fail(
                    "exchange.manage_as_app",
                    "Exchange.ManageAsApp app permission consented",
                    "Token roles claim is missing 'Exchange.ManageAsApp'. Without this, EXO returns 401 on every call.",
                    evidence: $"roles={roles ?? "(empty)"}",
                    remediation: "In the source tenant Entra portal, grant 'Office 365 Exchange Online → Exchange.ManageAsApp' application permission to the app and admin-consent. Then re-run."));
            }

            // ── Check: Microsoft Entra directory role for app-only EXO writes ──
            // Exchange.ManageAsApp is documented as "impersonation" only — it does
            // NOT grant any cmdlet permissions. EXO derives the session's RBAC from
            // the directory role information in the token (the "wids" claim).
            //
            // Without one of these directory roles, EXO returns 403
            // CmdletAccessDeniedException on every write cmdlet (New-DistributionGroup,
            // New-MailUser, New-MigrationBatch, Set-OrganizationRelationship, etc.)
            // even when New-ManagementRoleAssignment -App ... -Role "Distribution Groups"
            // (and similar) appear assigned via Get-ManagementRoleAssignment. Per the
            // Microsoft Learn docs for New-ManagementRoleAssignment: "If you use the
            // App parameter, you can't specify admin or user roles; you can only
            // specify application roles (for example, 'Application Mail.Read')."
            // Those Application-prefix roles are MS Graph / EWS only, not EXO cmdlets.
            //
            // Well-known role template IDs (https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/permissions-reference):
            //   29232cdf-9323-42fd-ade2-1d097af3e4de = Exchange Administrator
            //   31392ffb-586c-42d1-9346-e59415a2cc4e = Exchange Recipient Administrator
            //   62e90394-69f5-4237-9190-012177145e10 = Global Administrator
            const string ExchangeAdminRoleId          = "29232cdf-9323-42fd-ade2-1d097af3e4de";
            const string ExchangeRecipientAdminRoleId = "31392ffb-586c-42d1-9346-e59415a2cc4e";
            const string GlobalAdminRoleId            = "62e90394-69f5-4237-9190-012177145e10";

            var wids = claims.GetValueOrDefault("wids") ?? "";
            bool hasExchangeAdmin          = wids.Contains(ExchangeAdminRoleId,          StringComparison.OrdinalIgnoreCase);
            bool hasExchangeRecipientAdmin = wids.Contains(ExchangeRecipientAdminRoleId, StringComparison.OrdinalIgnoreCase);
            bool hasGlobalAdmin            = wids.Contains(GlobalAdminRoleId,            StringComparison.OrdinalIgnoreCase);

            if (hasExchangeAdmin || hasExchangeRecipientAdmin || hasGlobalAdmin)
            {
                var which = hasGlobalAdmin ? "Global Administrator"
                          : hasExchangeAdmin ? "Exchange Administrator"
                          : "Exchange Recipient Administrator";
                checks.Add(DiagCheck.Pass(
                    "exchange.directory_role",
                    "Microsoft Entra directory role assigned to app (required for EXO writes)",
                    $"Token wids claim includes {which} — EXO will accept write cmdlets from this app.",
                    evidence: $"wids={wids}"));
            }
            else
            {
                checks.Add(DiagCheck.Fail(
                    "exchange.directory_role",
                    "Microsoft Entra directory role assigned to app (required for EXO writes)",
                    "Token has NO Exchange Administrator / Exchange Recipient Administrator / Global Administrator " +
                    "in its 'wids' claim. Exchange.ManageAsApp alone is impersonation only — it does not grant any " +
                    "cmdlet permissions. Without a directory role, every EXO write cmdlet (New-DistributionGroup, " +
                    "New-MailUser, New-MigrationBatch, Set-OrganizationRelationship, etc.) returns 403 " +
                    "CmdletAccessDeniedException. Get-* read calls succeed because EXO impersonation grants implicit read.",
                    evidence: $"wids={(string.IsNullOrWhiteSpace(wids) ? "(empty — no directory roles assigned)" : wids)}",
                    remediation:
                        $"In the Microsoft Entra admin center for tenant {tenant.TenantId}: Roles and administrators → " +
                        $"open 'Exchange Recipient Administrator' (or 'Exchange Administrator' for broader scope) → " +
                        $"Add assignment → search for the app's enterprise application by display name " +
                        $"(AppId {tenant.AppClientId}, SP ObjectId is the value passed to New-ServicePrincipal -ObjectId) → " +
                        "Add. Token cache refresh takes up to 30 minutes; the platform's existing token is reused for " +
                        "its lifetime (≈75 min) so a service restart accelerates pickup. NOTE: 'New-ManagementRoleAssignment " +
                        "-App ... -Role \"Distribution Groups\"' (etc.) does NOT grant cmdlet access — Microsoft Learn " +
                        "documents that -App accepts only Application-prefix roles (e.g. 'Application Mail.Read'), which " +
                        "are MS Graph/EWS-only and don't include EXO management cmdlets. The directory role is the " +
                        "supported path for app-only EXO writes."));
            }
        }
        catch (Exception ex)
        {
            checks.Add(DiagCheck.Fail(
                "token.mint",
                "Acquire EXO access token",
                $"Token acquisition failed: {ex.Message}",
                evidence: ex.ToString()));
            report.Summary = "token.mint FAIL — cannot run any further checks.";
            return Ok(report);
        }

        // ── Check 3: EXO ServicePrincipal registered for this AppId ─────────
        await RunInvokeCheckAsync(
            checks,
            checkId: "exo.sp.self",
            description: $"EXO ServicePrincipal registered for this app ({tenant.AppClientId})",
            cmdlet: "Get-ServicePrincipal",
            parameters: new Dictionary<string, object> { ["Identity"] = tenant.AppClientId },
            tenant: tenant, cred: cred, ct: ct,
            interpretSuccess: results =>
            {
                if (results.Length > 0)
                {
                    var sp = results[0];
                    var dn = TryGetString(sp, "DisplayName") ?? "(no DisplayName)";
                    var oid = TryGetString(sp, "ServiceId") ?? TryGetString(sp, "AppId") ?? "(unknown)";
                    return ($"PASS — SP exists. DisplayName='{dn}' AppId/SvcId='{oid}'", true);
                }
                return ("FAIL — Get-ServicePrincipal returned 0 results.", false);
            },
            remediationOnFail: "On this tenant, run: Connect-ExchangeOnline; New-ServicePrincipal -AppId <yourAppId> -ObjectId <spObjectId>. The ObjectId is the AAD service principal object id, NOT the app registration object id.");

        // ── Check 4: RBAC role assignments (full inspection) ────────────────
        // We capture ALL scope and config fields per assignment so we can spot
        // restrictions that would block writes even when RecipientWriteScope is
        // "Organization" (e.g. Enabled=False, ConfigWriteScope=None on a role
        // assignment for a write-class role, or a Custom*Scope set to a stale RAS).
        var assignedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await RunInvokeCheckAsync(
            checks,
            checkId: "exo.rbac.assignments",
            description: $"RBAC role assignments for app {tenant.AppClientId}",
            cmdlet: "Get-ManagementRoleAssignment",
            parameters: new Dictionary<string, object> { ["RoleAssignee"] = tenant.AppClientId },
            tenant: tenant, cred: cred, ct: ct,
            interpretSuccess: results =>
            {
                if (results.Length == 0)
                    return ("FAIL — no role assignments found for this app.", false);

                var sb = new StringBuilder();
                var problems = new List<string>();
                int disabled = 0;
                int restrictedRead = 0;
                int restrictedWrite = 0;
                int customScopeSet = 0;
                int ouScopeSet = 0;
                int configWriteRestricted = 0;

                foreach (var a in results)
                {
                    var name = TryGetString(a, "Name") ?? "?";
                    var role = TryGetString(a, "Role") ?? "?";
                    var roleAssignee = TryGetString(a, "RoleAssignee") ?? "?";
                    var roleAssigneeName = TryGetString(a, "RoleAssigneeName") ?? "?";
                    var roleAssigneeType = TryGetString(a, "RoleAssigneeType") ?? "?";
                    var assignmentMethod = TryGetString(a, "AssignmentMethod") ?? "?";
                    var rrScope = TryGetString(a, "RecipientReadScope") ?? "";
                    var rwScope = TryGetString(a, "RecipientWriteScope") ?? "";
                    var customRrScope = TryGetString(a, "CustomRecipientReadScope") ?? "";
                    var customRwScope = TryGetString(a, "CustomRecipientWriteScope") ?? "";
                    var ouScope = TryGetString(a, "RecipientOrganizationalUnitScope") ?? "";
                    var configReadScope = TryGetString(a, "ConfigReadScope") ?? "";
                    var configWriteScope = TryGetString(a, "ConfigWriteScope") ?? "";
                    var enabled = TryGetString(a, "Enabled") ?? "?";
                    var app = TryGetString(a, "App") ?? "";
                    var user = TryGetString(a, "User") ?? "";

                    if (role != "?")
                        assignedRoles.Add(role);

                    sb.Append($"[Name={name} Role={role} ");
                    sb.Append($"Enabled={enabled} ");
                    sb.Append($"AssigneeType={roleAssigneeType} ");
                    sb.Append($"Assignee={roleAssignee} ");
                    sb.Append($"AssigneeName={roleAssigneeName} ");
                    sb.Append($"AssignmentMethod={assignmentMethod} ");
                    sb.Append($"App={(string.IsNullOrEmpty(app) ? "(null)" : app)} ");
                    sb.Append($"User={(string.IsNullOrEmpty(user) ? "(null)" : user)} ");
                    sb.Append($"RRScope={(string.IsNullOrEmpty(rrScope) ? "(blank)" : rrScope)} ");
                    sb.Append($"RWScope={(string.IsNullOrEmpty(rwScope) ? "(blank)" : rwScope)} ");
                    sb.Append($"CustomRR={(string.IsNullOrEmpty(customRrScope) ? "(blank)" : customRrScope)} ");
                    sb.Append($"CustomRW={(string.IsNullOrEmpty(customRwScope) ? "(blank)" : customRwScope)} ");
                    sb.Append($"OUScope={(string.IsNullOrEmpty(ouScope) ? "(blank)" : ouScope)} ");
                    sb.Append($"ConfigRead={(string.IsNullOrEmpty(configReadScope) ? "(blank)" : configReadScope)} ");
                    sb.Append($"ConfigWrite={(string.IsNullOrEmpty(configWriteScope) ? "(blank)" : configWriteScope)}] ");

                    if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(enabled, "False", StringComparison.Ordinal))
                    {
                        disabled++;
                        problems.Add($"{name} is Enabled=False");
                    }

                    // RecipientReadScope: should be Organization, MyGAL, NotApplicable, or blank.
                    // Anything else (CustomRecipientScope, MyDistributionGroups, etc.) restricts even reads.
                    if (!string.IsNullOrEmpty(rrScope) &&
                        !rrScope.Equals("Organization", StringComparison.OrdinalIgnoreCase) &&
                        !rrScope.Equals("MyGAL", StringComparison.OrdinalIgnoreCase) &&
                        !rrScope.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase) &&
                        !rrScope.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        restrictedRead++;
                        problems.Add($"{name} has restricted RecipientReadScope={rrScope}");
                    }

                    if (!string.IsNullOrEmpty(rwScope) &&
                        !rwScope.Equals("Organization", StringComparison.OrdinalIgnoreCase) &&
                        !rwScope.Equals("MyGAL", StringComparison.OrdinalIgnoreCase) &&
                        !rwScope.Equals("NotApplicable", StringComparison.OrdinalIgnoreCase) &&
                        !rwScope.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        restrictedWrite++;
                        problems.Add($"{name} has restricted RecipientWriteScope={rwScope}");
                    }

                    if (!string.IsNullOrEmpty(customRrScope) || !string.IsNullOrEmpty(customRwScope))
                    {
                        customScopeSet++;
                        problems.Add($"{name} has Custom*Scope set (RR={customRrScope} RW={customRwScope})");
                    }

                    if (!string.IsNullOrEmpty(ouScope))
                    {
                        ouScopeSet++;
                        problems.Add($"{name} has RecipientOrganizationalUnitScope set ({ouScope})");
                    }

                    // ConfigWriteScope=None on a write-class role (Migration, Mail Recipient Creation,
                    // Distribution Groups, etc.) is fine — it only writes recipient objects, not config.
                    // But ConfigWriteScope=None on Organization Configuration is a real restriction.
                    if (role.Equals("Organization Configuration", StringComparison.OrdinalIgnoreCase) &&
                        configWriteScope.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        configWriteRestricted++;
                        problems.Add($"{name} ('Organization Configuration') has ConfigWriteScope=None");
                    }
                }

                var prefix = (disabled + restrictedRead + restrictedWrite + customScopeSet + ouScopeSet + configWriteRestricted) > 0
                    ? $"WARN — {results.Length} assignment(s); issues: " + string.Join("; ", problems) + ". Details: "
                    : $"PASS — {results.Length} assignment(s) — all scopes look unrestricted. Details: ";

                return (prefix + sb, true);
            },
            remediationOnFail: "Assign at minimum: 'Mail Recipients', 'Distribution Groups', 'Migration', 'Organization Configuration', 'Recipient Policies', 'View-Only Configuration', 'View-Only Recipients' to the service principal via New-ManagementRoleAssignment -App <appId> -Role <role>. If a restricted RecipientWriteScope is shown above, recreate the role assignment WITHOUT a -RecipientOrganizationalUnitScope or -CustomRecipientWriteScope to grant org-wide write.");

        // ── Check 4b: Verify the cmdlet New-DistributionGroup is actually
        // covered by the assigned roles (RoleEntries inspection). RBAC can be
        // present at Organization scope but still 403 if the role's RoleEntries
        // were tampered with (Remove-ManagementRoleEntry) or if the cmdlet
        // genuinely lives in a different role we haven't assigned.
        var rolesToCheck = new[] { "Mail Recipient Creation", "Distribution Groups", "Mail Recipients", "Migration" };
        foreach (var roleName in rolesToCheck)
        {
            var checkId = $"exo.rbac.role_entries.{roleName.ToLowerInvariant().Replace(' ', '_')}";
            await RunInvokeCheckAsync(
                checks,
                checkId: checkId,
                description: $"Role entries for '{roleName}'",
                cmdlet: "Get-ManagementRole",
                parameters: new Dictionary<string, object> { ["Identity"] = roleName },
                tenant: tenant, cred: cred, ct: ct,
                interpretSuccess: results =>
                {
                    if (results.Length == 0)
                        return ($"WARN — role '{roleName}' not found in this org.", true);

                    var role = results[0];
                    var roleEntries = new List<string>();
                    if (role.TryGetProperty("RoleEntries", out var entries))
                    {
                        if (entries.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var e in entries.EnumerateArray())
                            {
                                var s = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
                                if (!string.IsNullOrWhiteSpace(s)) roleEntries.Add(s!);
                            }
                        }
                        else if (entries.ValueKind == JsonValueKind.String)
                        {
                            var s = entries.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) roleEntries.Add(s!);
                        }
                    }

                    bool isAssigned = assignedRoles.Contains(roleName);
                    var entriesPreview = roleEntries.Count > 0
                        ? (roleEntries.Count <= 30
                            ? string.Join(" | ", roleEntries)
                            : string.Join(" | ", roleEntries.Take(30)) + $" ... ({roleEntries.Count - 30} more)")
                        : "(no entries surfaced — RoleEntries may not be returned by REST)";

                    bool hasNewDg = roleEntries.Any(e => e.IndexOf("New-DistributionGroup", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hasNewMu = roleEntries.Any(e => e.IndexOf("New-MailUser", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hasEnableMu = roleEntries.Any(e => e.IndexOf("Enable-MailUser", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hasNewMb = roleEntries.Any(e => e.IndexOf("New-MailUser", StringComparison.OrdinalIgnoreCase) >= 0);

                    var msg = $"role '{roleName}' (assigned-to-app={(isAssigned ? "yes" : "NO")}, entries={roleEntries.Count}, has-New-DistributionGroup={hasNewDg}, has-New-MailUser={hasNewMu}, has-Enable-MailUser={hasEnableMu}). Entries: {entriesPreview}";
                    return ("PASS — " + msg, true);
                },
                remediationOnFail: $"Inspect via Get-ManagementRole '{roleName}' | Format-List Name, RoleEntries. If RoleEntries is missing the cmdlet, the role has been edited and writes will fail. Restore the role definition or assign a role that contains the cmdlet.");
        }

        // ── Check 5: Cross-tenant migration SP registered in this EXO ───────
        var crossTenantAppId = report.CrossTenantAppId;
        await RunInvokeCheckAsync(
            checks,
            checkId: "exo.sp.crosstenant",
            description: $"EXO ServicePrincipal registered for cross-tenant Mailbox Migration app ({crossTenantAppId})",
            cmdlet: "Get-ServicePrincipal",
            parameters: new Dictionary<string, object> { ["Identity"] = crossTenantAppId },
            tenant: tenant, cred: cred, ct: ct,
            interpretSuccess: results =>
            {
                if (results.Length > 0)
                {
                    var sp = results[0];
                    var dn = TryGetString(sp, "DisplayName") ?? "(no DisplayName)";
                    return ($"PASS — Cross-tenant migration SP exists. DisplayName='{dn}'", true);
                }
                return ("FAIL — Cross-tenant migration SP not found in this tenant's EXO.", false);
            },
            remediationOnFail: $"In this tenant, ensure the cross-tenant migration app SP is registered. The AppId is '{crossTenantAppId}'. Use: Get-ServicePrincipal -Identity <appid>; if missing, run New-ServicePrincipal -AppId <appid> -ObjectId <ServicePrincipalObjectId from Entra portal>.");

        // ── Check 6: Organization relationships ─────────────────────────────
        await RunInvokeCheckAsync(
            checks,
            checkId: "exo.orgrel",
            description: "Organization relationships",
            cmdlet: "Get-OrganizationRelationship",
            parameters: new Dictionary<string, object>(),
            tenant: tenant, cred: cred, ct: ct,
            interpretSuccess: results =>
            {
                if (results.Length == 0)
                    return ("WARN — no OrganizationRelationships found. Expected one for cross-tenant migration.", true);

                var sb = new StringBuilder();
                foreach (var r in results)
                {
                    var name = TryGetString(r, "Name") ?? "?";
                    var enabled = TryGetString(r, "Enabled") ?? "?";
                    var moveEnabled = TryGetString(r, "MailboxMoveEnabled") ?? "?";
                    var moveCap = TryGetString(r, "MailboxMoveCapability") ?? "?";
                    var oauthApp = TryGetString(r, "OAuthApplicationId");
                    var domains = string.Join(",", EnumerateStringArray(r, "DomainNames"));
                    var scopes = string.Join(",", EnumerateStringArray(r, "MailboxMovePublishedScopes"));
                    sb.Append($"[Name={name} Domains={domains} Enabled={enabled} MoveEnabled={moveEnabled} MoveCap={moveCap} OAuthApp={oauthApp ?? "(null)"} PubScopes={scopes}] ");
                }
                return ($"PASS — {results.Length} relationship(s): {sb}", true);
            },
            remediationOnFail: "Manually check via Get-OrganizationRelationship | Format-List Name,DomainNames,MailboxMoveEnabled,MailboxMoveCapability,OAuthApplicationId,MailboxMovePublishedScopes. The platform's worker creates these automatically — if they're missing the worker hasn't run yet for this tenant pair.");

        // ── Check 7: Migration endpoints ────────────────────────────────────
        await RunInvokeCheckAsync(
            checks,
            checkId: "exo.endpoint",
            description: "Migration endpoints",
            cmdlet: "Get-MigrationEndpoint",
            parameters: new Dictionary<string, object>(),
            tenant: tenant, cred: cred, ct: ct,
            interpretSuccess: results =>
            {
                if (results.Length == 0)
                    return ("WARN — no Migration endpoints found. Target tenant must have one for inbound MRS.", true);

                var sb = new StringBuilder();
                foreach (var ep in results)
                {
                    var ident = TryGetString(ep, "Identity") ?? TryGetString(ep, "Name") ?? "?";
                    var epType = TryGetString(ep, "EndpointType") ?? "?";
                    var appId = TryGetString(ep, "ApplicationId");
                    var remoteTenant = TryGetString(ep, "RemoteTenant");
                    sb.Append($"[Identity={ident} Type={epType} AppId={appId ?? "(null)"} RemoteTenant={remoteTenant ?? "(null)"}] ");
                }
                return ($"PASS — {results.Length} endpoint(s): {sb}", true);
            },
            remediationOnFail: "Migration endpoint is created on the TARGET tenant only. The platform's worker creates this. If missing, retry the batch — the worker will create it.");

        // ── Check 8: Accepted domains — every verified domain in the tenant ────
        // For cross-tenant migration, source mailbox primary SMTP domains MUST be in the
        // source tenant's accepted-domain list with DomainType=Authoritative or InternalRelay
        // and Verified=True. If a source mailbox has PrimarySmtpAddress at an unverified
        // domain, MRS fails CSV row validation with `MigrationCSVRowValidationException: 0x80070057`.
        await RunInvokeCheckAsync(
            checks,
            checkId: "exo.accepted_domains",
            description: "Verified accepted domains",
            cmdlet: "Get-AcceptedDomain",
            parameters: new Dictionary<string, object>(),
            tenant: tenant, cred: cred, ct: ct,
            interpretSuccess: results =>
            {
                if (results.Length == 0)
                    return ("WARN — no accepted domains returned (unexpected).", true);
                var sb = new StringBuilder();
                foreach (var d in results)
                {
                    var name = TryGetString(d, "DomainName") ?? "?";
                    var type = TryGetString(d, "DomainType") ?? "?";
                    var defaultDomain = TryGetString(d, "Default") ?? "?";
                    sb.Append($"[{name} type={type} default={defaultDomain}] ");
                }
                return ($"PASS — {results.Length} domain(s): {sb}", true);
            },
            remediationOnFail: "If a source mailbox's PrimarySmtpAddress is at a domain not listed here as Authoritative/InternalRelay, MRS will fail CSV row validation. Verify the domain in M365 admin center → Settings → Domains.");

        // ── Check 8: Smoke write test (DG creation, then immediate cleanup) ─
        var smokeName = $"PREREQ-CHECK-TEST-{Guid.NewGuid():N}";
        var smokeSmtp = !string.IsNullOrWhiteSpace(tenant.OnMicrosoftDomain)
            ? $"{smokeName}@{tenant.OnMicrosoftDomain}.onmicrosoft.com"
            : $"{smokeName}@example.invalid";

        ExoRawInvokeResult? createResult = null;
        ExoRawInvokeResult? removeResult = null;
        try
        {
            createResult = await _exo.InvokeCommandRawAsync(
                tenant.TenantId,
                "New-DistributionGroup",
                new Dictionary<string, object>
                {
                    ["Name"] = smokeName,
                    ["Type"] = "Security",
                    ["PrimarySmtpAddress"] = smokeSmtp,
                },
                cred, ct);

            if (createResult.IsSuccess)
            {
                checks.Add(DiagCheck.Pass(
                    "exo.smoke_write",
                    "Smoke-write test: New-DistributionGroup",
                    $"Writes work. Created '{smokeName}' (smtp={smokeSmtp}).",
                    evidence: BuildRawEvidence(createResult)));
            }
            else
            {
                var http = createResult.HttpStatus;
                var msg = createResult.ParsedErrorMessage ?? createResult.BodyTextPreview ?? createResult.ReasonPhrase ?? "(no message)";

                if (http == 403)
                {
                    checks.Add(DiagCheck.Fail(
                        "exo.smoke_write",
                        "Smoke-write test: New-DistributionGroup",
                        $"Writes BLOCKED — HTTP 403. X-ExceptionType={createResult.XExceptionType ?? "(none)"}. Message: {msg}",
                        evidence: BuildRawEvidence(createResult),
                        remediation: "RBAC has insufficient scope for writes. Check the 'exo.rbac.assignments' check above for restricted RecipientWriteScope. Likely fix: re-assign the roles WITHOUT -RecipientOrganizationalUnitScope so RecipientWriteScope is 'Organization'."));
                }
                else
                {
                    checks.Add(DiagCheck.Fail(
                        "exo.smoke_write",
                        "Smoke-write test: New-DistributionGroup",
                        $"Smoke-write failed — HTTP {http} {createResult.ReasonPhrase}. {msg}",
                        evidence: BuildRawEvidence(createResult)));
                }
            }
        }
        catch (Exception ex)
        {
            checks.Add(DiagCheck.Unknown(
                "exo.smoke_write",
                "Smoke-write test: New-DistributionGroup",
                $"Transport error: {ex.Message}",
                evidence: ex.ToString()));
        }
        finally
        {
            // Always attempt cleanup — even if the create returned 200, even if we don't
            // know whether it created something (e.g. timeout). Suppress all errors.
            if (createResult is not null && createResult.IsSuccess)
            {
                try
                {
                    removeResult = await _exo.InvokeCommandRawAsync(
                        tenant.TenantId,
                        "Remove-DistributionGroup",
                        new Dictionary<string, object>
                        {
                            ["Identity"] = smokeName,
                            ["Confirm"] = false,
                        },
                        cred, ct);
                    _logger.LogInformation(
                        "Diag smoke-write cleanup for tenant {Tid}: HTTP {Status} ({Cleanup})",
                        tenant.TenantId, removeResult.HttpStatus,
                        removeResult.IsSuccess ? "removed" : (removeResult.ParsedErrorMessage ?? "?"));
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx,
                        "Diag smoke-write cleanup failed for {Tid} group {Group} — manual delete required.",
                        tenant.TenantId, smokeName);
                }
            }
        }

        // ── Compose summary ────────────────────────────────────────────────
        var failCount = checks.Count(c => c.Status == "fail");
        var warnCount = checks.Count(c => c.Status == "warn");
        var passCount = checks.Count(c => c.Status == "pass");
        var unkCount = checks.Count(c => c.Status == "unknown");
        report.Summary = $"{passCount} pass / {failCount} fail / {warnCount} warn / {unkCount} unknown";
        report.PassCount = passCount;
        report.FailCount = failCount;
        report.WarnCount = warnCount;
        report.UnknownCount = unkCount;

        return Ok(report);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task RunInvokeCheckAsync(
        List<DiagCheck> checks,
        string checkId,
        string description,
        string cmdlet,
        Dictionary<string, object> parameters,
        Tenant tenant,
        TokenCredential cred,
        CancellationToken ct,
        Func<JsonElement[], (string Message, bool IsPass)> interpretSuccess,
        string? remediationOnFail = null)
    {
        ExoRawInvokeResult result;
        try
        {
            result = await _exo.InvokeCommandRawAsync(tenant.TenantId, cmdlet, parameters, cred, ct);
        }
        catch (Exception ex)
        {
            checks.Add(DiagCheck.Unknown(
                checkId, description,
                $"Transport error invoking {cmdlet}: {ex.Message}",
                evidence: ex.ToString()));
            return;
        }

        if (result.IsSuccess)
        {
            var (msg, isPass) = interpretSuccess(result.Results);
            var check = isPass
                ? (msg.StartsWith("WARN", StringComparison.OrdinalIgnoreCase)
                    ? DiagCheck.Warn(checkId, description, msg, evidence: BuildRawEvidence(result))
                    : DiagCheck.Pass(checkId, description, msg, evidence: BuildRawEvidence(result)))
                : DiagCheck.Fail(checkId, description, msg, evidence: BuildRawEvidence(result), remediation: remediationOnFail);
            checks.Add(check);
        }
        else
        {
            var msg = result.ParsedErrorMessage ?? result.BodyTextPreview ?? result.ReasonPhrase ?? "(no message)";
            // 404 / not-found from EXO is a real fail for these checks (object doesn't exist).
            checks.Add(DiagCheck.Fail(
                checkId, description,
                $"{cmdlet} → HTTP {result.HttpStatus} {result.ReasonPhrase}. {msg}",
                evidence: BuildRawEvidence(result),
                remediation: remediationOnFail));
        }
    }

    private static string BuildRawEvidence(ExoRawInvokeResult r)
    {
        return $"cmdlet={r.CmdletName} http={r.HttpStatus} reason={r.ReasonPhrase} " +
               $"bodyLen={r.BodyByteLength} bodyHex={r.BodyHexPreview} " +
               $"x-exception={r.XExceptionType ?? "(none)"} " +
               $"request-id={r.RequestId ?? "(none)"} " +
               (r.WwwAuthenticate is not null ? $"www-auth={r.WwwAuthenticate} " : "") +
               $"bodyText={r.BodyTextPreview ?? "(empty)"}";
    }

    private static Dictionary<string, string?> ParseJwtClaims(string jwt)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return dict;
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                dict[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.ToString(),
                    JsonValueKind.Array => string.Join(" ", p.Value.EnumerateArray().Select(e => e.ToString())),
                    _ => p.Value.ToString(),
                };
            }
        }
        catch { /* best effort */ }
        return dict;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            JsonValueKind.Object => p.ToString(),
            JsonValueKind.Array => p.ToString(),
            _ => null,
        };
    }

    private static IEnumerable<string> EnumerateStringArray(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) yield break;
        if (!el.TryGetProperty(name, out var p)) yield break;
        if (p.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in p.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(s)) yield return s!;
            }
        }
        else if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (!string.IsNullOrWhiteSpace(s)) yield return s!;
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class TenantPrereqReport
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = "";
    public string AadTenantId { get; set; } = "";
    public string AppClientId { get; set; } = "";
    public string? OnMicrosoftDomain { get; set; }
    public string CrossTenantAppId { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public string Summary { get; set; } = "";
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public int WarnCount { get; set; }
    public int UnknownCount { get; set; }
    public List<DiagCheck> Checks { get; set; } = new();
}

public sealed class DiagCheck
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";   // pass | fail | warn | unknown
    public string Message { get; set; } = "";
    public string? Evidence { get; set; }
    public string? Remediation { get; set; }

    public static DiagCheck Pass(string id, string desc, string msg, string? evidence = null) =>
        new() { Id = id, Description = desc, Status = "pass", Message = msg, Evidence = evidence };

    public static DiagCheck Fail(string id, string desc, string msg, string? evidence = null, string? remediation = null) =>
        new() { Id = id, Description = desc, Status = "fail", Message = msg, Evidence = evidence, Remediation = remediation };

    public static DiagCheck Warn(string id, string desc, string msg, string? evidence = null, string? remediation = null) =>
        new() { Id = id, Description = desc, Status = "warn", Message = msg, Evidence = evidence, Remediation = remediation };

    public static DiagCheck Unknown(string id, string desc, string msg, string? evidence = null) =>
        new() { Id = id, Description = desc, Status = "unknown", Message = msg, Evidence = evidence };
}
