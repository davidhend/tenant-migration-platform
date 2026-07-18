using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Models.ODataErrors;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TenantsController : ControllerBase
{
    private static readonly string[] ExoScopes = ["https://outlook.office365.com/.default"];

    private readonly ITenantRepository _tenants;
    private readonly IAuditRepository _audit;
    private readonly IGraphClientFactory _graphFactory;
    private readonly ITenantCredentialFactory _credentialFactory;
    private readonly IKeyVaultCredentialService _keyVault;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        ITenantRepository tenants,
        IAuditRepository audit,
        IGraphClientFactory graphFactory,
        ITenantCredentialFactory credentialFactory,
        IKeyVaultCredentialService keyVault,
        ICurrentUserService currentUser,
        ILogger<TenantsController> logger)
    {
        _tenants = tenants;
        _audit = audit;
        _graphFactory = graphFactory;
        _credentialFactory = credentialFactory;
        _keyVault = keyVault;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>Get all registered tenants.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await _tenants.GetAllAsync(ct));

    /// <summary>Get a specific tenant by ID.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenant = await _tenants.GetByIdAsync(id, ct);
        return tenant is null ? NotFound() : Ok(tenant);
    }

    /// <summary>Register a new tenant connection.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest req, CancellationToken ct)
    {
        var tenant = new Tenant
        {
            DisplayName = req.DisplayName,
            TenantId = req.TenantId,
            Role = req.Role,
            AppClientId = req.AppClientId,
            AuthMethod = req.AuthMethod,
            ClientSecretHint = req.ClientSecret?.Length >= 4 ? req.ClientSecret[^4..] : null,
            // ClientSecretPlain is runtime-only; never persisted to DB
            ClientSecretPlain = req.ClientSecret,
        };

        await _tenants.AddAsync(tenant, ct);
        await _tenants.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "TENANT_ADDED",
            Resource = $"tenants/{tenant.Id}",
            Actor = _currentUser.UserName,
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("Tenant {Id} ({Name}) registered.", tenant.Id, tenant.DisplayName);
        return CreatedAtAction(nameof(GetById), new { id = tenant.Id }, tenant);
    }

    /// <summary>
    /// Update the authentication credentials for a tenant.
    /// When Key Vault is enabled, certificate bytes and the client secret are
    /// written to Key Vault and are NOT stored in the database.
    /// The thumbprint and auth method are always written to the database for
    /// display and auditing purposes regardless of Key Vault state.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{id:guid}/credentials")]
    public async Task<IActionResult> UpdateCredentials(Guid id, [FromBody] UpdateCredentialsRequest req, CancellationToken ct)
    {
        if (!await _tenants.ExistsAsync(id, ct)) return NotFound();

        string? secretHint = null;
        if (req.AuthMethod == AuthMethod.Secret && req.ClientSecret is not null)
            secretHint = req.ClientSecret.Length >= 4 ? req.ClientSecret[^4..] : null;

        // When Key Vault is enabled, store the sensitive values there and pass
        // nulls to the database so the DB is never the source of truth for secrets.
        // The thumbprint is informational (no secret material) and always goes to DB.
        if (_keyVault.IsEnabled)
        {
            await _keyVault.StoreCredentialsAsync(
                id,
                req.ClientCertificateBase64,
                req.ClientCertificatePassword,
                req.ClientSecret,
                ct);

            await _tenants.UpdateCredentialsAsync(
                id,
                req.AuthMethod,
                req.AppClientId,
                secretHint,
                clientCertificateBase64: null,     // Key Vault is the source of truth
                clientCertificatePassword: null,   // Key Vault is the source of truth
                req.ClientCertificateThumbprint,
                ct);

            _logger.LogInformation(
                "Credentials for tenant {Id} persisted to Key Vault; DB updated with thumbprint/auth-method only.",
                id);
        }
        else
        {
            // Key Vault disabled — preserve existing DB storage behaviour
            await _tenants.UpdateCredentialsAsync(
                id,
                req.AuthMethod,
                req.AppClientId,
                secretHint,
                req.ClientCertificateBase64,
                req.ClientCertificatePassword,
                req.ClientCertificateThumbprint,
                ct);
        }

        await _audit.AddAsync(new AuditEvent
        {
            Action = "TENANT_CREDENTIALS_UPDATED",
            Resource = $"tenants/{id}",
            Actor = _currentUser.UserName,
        }, ct);
        await _audit.SaveAsync(ct);

        _logger.LogInformation("Credentials updated for tenant {Id}.", id);
        return NoContent();
    }

    /// <summary>
    /// Verify the tenant's enterprise app connection by calling the Microsoft Graph API
    /// with the stored credentials. Returns a structured result rather than HTTP 4xx/5xx
    /// because the HTTP call itself always succeeds — the verification outcome is the payload.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
    {
        var tenant = await _tenants.GetByIdAsync(id, ct);
        if (tenant is null) return NotFound();

        // Load credentials from Key Vault (returns all-null when KV is disabled)
        var (kvCertBase64, kvCertPassword, kvSecret) = await _keyVault.LoadCredentialsAsync(id, ct);

        // Resolve the effective credential values by merging KV results with DB/model values.
        // KV values take priority; model properties are the fallback.
        var effectiveCertBase64 = kvCertBase64  ?? tenant.ClientCertificateBase64;
        var effectiveSecret     = kvSecret      ?? tenant.ClientSecretPlain;

        // Guard: secret auth requires a resolvable secret.
        // When Key Vault is enabled the secret may have been loaded from KV above;
        // when it is disabled the caller must have re-supplied it at runtime via
        // UpdateCredentials because ClientSecretPlain is never persisted to the DB.
        if (tenant.AuthMethod == AuthMethod.Secret && string.IsNullOrWhiteSpace(effectiveSecret))
        {
            return BadRequest(new
            {
                message = "Client secret credentials are not available. " +
                          (_keyVault.IsEnabled
                              ? "The secret was not found in Key Vault. Re-upload via PUT /api/tenants/{id}/credentials."
                              : "Re-supply the secret via PUT /api/tenants/{id}/credentials — " +
                                "secrets are never stored and must be provided on each verification.")
            });
        }

        // Guard: certificate auth requires resolvable certificate bytes.
        if (tenant.AuthMethod == AuthMethod.Certificate && string.IsNullOrWhiteSpace(effectiveCertBase64))
        {
            return BadRequest(new
            {
                message = "No certificate is configured for this tenant. " +
                          "Upload the PFX certificate via PUT /api/tenants/{id}/credentials before verifying."
            });
        }

        // Mark as pending while the Graph calls are in-flight.
        await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Pending, null, false, ct);

        _logger.LogInformation(
            "Starting connection verification for tenant {TenantId} ({DisplayName}) using {AuthMethod}. " +
            "Credentials sourced from: {CredentialSource}.",
            tenant.TenantId, tenant.DisplayName, tenant.AuthMethod,
            _keyVault.IsEnabled ? "Key Vault" : "database");

        try
        {
            // Use the override overload so Key Vault values supersede DB values when present.
            var graphClient = _graphFactory.CreateForTenant(
                tenant,
                certBase64Override:  kvCertBase64,
                certPasswordOverride: kvCertPassword,
                secretOverride:      kvSecret);

            // GET /organization — requires a valid token; any working credential with
            // at minimum the default scope will succeed. Confirms token acquisition.
            var orgCollection = await graphClient.Organization.GetAsync(cancellationToken: ct);

            var org = orgCollection?.Value?.FirstOrDefault();
            var orgName = org?.DisplayName ?? tenant.DisplayName;

            // Hybrid detection: organizations synced from on-prem AD (Entra
            // Connect) report onPremisesSyncEnabled=true. Persisted so a hybrid
            // TARGET tenant can drive the hybrid-handoff suggestion.
            var dirSync = org?.OnPremisesSyncEnabled == true;
            await _tenants.UpdateDirectorySyncAsync(id, dirSync, ct);
            if (dirSync)
                _logger.LogInformation(
                    "Tenant {TenantId} ({DisplayName}): Entra Connect (onPremisesSyncEnabled) detected.",
                    tenant.TenantId, tenant.DisplayName);

            // GET /domains — requires Domain.Read.All. A successful response confirms
            // at least this permission has been admin-consented.
            // Also extracts the initial onmicrosoft.com domain prefix for SPO admin URL derivation.
            var domainsCollection = await graphClient.Domains.GetAsync(cancellationToken: ct);

            var initialDomain = domainsCollection?.Value?
                .FirstOrDefault(d => d.IsInitial == true &&
                                     d.Id != null &&
                                     d.Id.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase));

            if (initialDomain?.Id is not null)
            {
                var prefix = initialDomain.Id[..initialDomain.Id.LastIndexOf(".onmicrosoft.com",
                    StringComparison.OrdinalIgnoreCase)];
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    await _tenants.UpdateOnMicrosoftDomainAsync(id, prefix, ct);
                    _logger.LogInformation(
                        "Tenant {TenantId} ({DisplayName}): detected OnMicrosoftDomain = '{Prefix}'.",
                        tenant.TenantId, tenant.DisplayName, prefix);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Tenant {TenantId} ({DisplayName}): no initial .onmicrosoft.com domain found in GET /domains response.",
                    tenant.TenantId, tenant.DisplayName);
            }

            var verifiedAt = DateTime.UtcNow;
            await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Connected, verifiedAt, true, ct);

            await _audit.AddAsync(new AuditEvent
            {
                Action = "TENANT_VERIFIED",
                Resource = $"tenants/{id}",
                Actor = _currentUser.UserName,
            }, ct);
            await _audit.SaveAsync(ct);

            _logger.LogInformation(
                "Tenant {TenantId} ({DisplayName}) verified successfully. Organization: {OrgName}.",
                tenant.TenantId, tenant.DisplayName, orgName);

            return Ok(new VerifyConnectionResponse(
                Success: true,
                Message: "Connection verified. App has the required Graph permissions.",
                OrganizationName: orgName,
                VerifiedAt: verifiedAt));
        }
        catch (AuthenticationFailedException ex)
        {
            await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Failed, null, false, ct);

            _logger.LogWarning(
                ex,
                "Authentication failed for tenant {TenantId} ({DisplayName}).",
                tenant.TenantId, tenant.DisplayName);

            return Ok(new VerifyConnectionResponse(
                Success: false,
                Message: "Authentication failed — verify the TenantId, AppClientId, and credentials are correct."));
        }
        catch (ODataError odataError) when (odataError.ResponseStatusCode == 403)
        {
            await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Failed, null, false, ct);

            _logger.LogWarning(
                "Tenant {TenantId} ({DisplayName}) authenticated but lacks required Graph permissions (403).",
                tenant.TenantId, tenant.DisplayName);

            return Ok(new VerifyConnectionResponse(
                Success: false,
                Message: "App authenticated but lacks required Graph permissions. Grant admin consent in Azure AD."));
        }
        catch (ODataError odataError) when (odataError.ResponseStatusCode == 401)
        {
            await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Failed, null, false, ct);

            _logger.LogWarning(
                "Tenant {TenantId} ({DisplayName}) returned 401 from Graph — token is invalid or expired.",
                tenant.TenantId, tenant.DisplayName);

            return Ok(new VerifyConnectionResponse(
                Success: false,
                Message: "Authentication failed — verify the TenantId, AppClientId, and credentials are correct."));
        }
        catch (Azure.RequestFailedException ex)
        {
            await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Failed, null, false, ct);

            _logger.LogWarning(
                ex,
                "Azure request failed for tenant {TenantId} ({DisplayName}): {Status} {ErrorCode}.",
                tenant.TenantId, tenant.DisplayName, ex.Status, ex.ErrorCode);

            return Ok(new VerifyConnectionResponse(
                Success: false,
                Message: "Authentication failed — verify the TenantId, AppClientId, and credentials are correct."));
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by GraphClientFactory when credentials are structurally invalid
            // (e.g., bad base64, cert load failure). Already guarded above for the
            // missing-credential case, but malformed data can still reach here.
            _logger.LogWarning(
                ex,
                "Invalid credential configuration for tenant {TenantId} ({DisplayName}).",
                tenant.TenantId, tenant.DisplayName);

            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            await _tenants.UpdateConnectionStatusAsync(id, ConnectionStatus.Failed, null, false, ct);

            _logger.LogError(
                ex,
                "Unexpected error verifying tenant {TenantId} ({DisplayName}).",
                tenant.TenantId, tenant.DisplayName);

            return Ok(new VerifyConnectionResponse(
                Success: false,
                Message: ex.Message));
        }
    }

    /// <summary>
    /// Acquire an EXO token for this tenant and decode its JWT claims.
    /// Use this to diagnose EXO 401 "invalid_token" errors — the response shows
    /// whether <c>Exchange.ManageAsApp</c> is present in the token's <c>roles</c> claim.
    /// The raw token is never returned; only decoded claim values are exposed.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("{id:guid}/diagnose-exo")]
    public async Task<IActionResult> DiagnoseExo(Guid id, CancellationToken ct)
    {
        var tenant = await _tenants.GetByIdAsync(id, ct);
        if (tenant is null) return NotFound();

        var (kvCertBase64, kvCertPassword, kvSecret) = await _keyVault.LoadCredentialsAsync(id, ct);

        TokenCredential credential;
        try
        {
            credential = _credentialFactory.CreateCredential(tenant, kvCertBase64, kvCertPassword, kvSecret);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = $"Credentials not available: {ex.Message}" });
        }

        AccessToken tokenResult;
        try
        {
            tokenResult = await credential.GetTokenAsync(new TokenRequestContext(ExoScopes), ct);
        }
        catch (AuthenticationFailedException ex)
        {
            return Ok(new
            {
                success = false,
                error = "Token acquisition failed",
                detail = ex.Message,
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                error = "Token acquisition failed",
                detail = ex.Message,
            });
        }

        // Decode JWT payload — split on '.' and base64url-decode the middle segment
        var parts = tokenResult.Token.Split('.');
        if (parts.Length < 2)
        {
            return Ok(new { success = false, error = "Token is not a valid JWT (fewer than 2 segments)." });
        }

        JsonDocument payload;
        try
        {
            var padded = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            payload = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = "Failed to decode JWT payload", detail = ex.Message });
        }

        using (payload)
        {
            var root = payload.RootElement;

            var aud = root.TryGetProperty("aud", out var audProp) ? audProp.GetString() : null;
            var appid = root.TryGetProperty("appid", out var appidProp) ? appidProp.GetString()
                      : root.TryGetProperty("azp", out var azpProp) ? azpProp.GetString() : null;
            var tid = root.TryGetProperty("tid", out var tidProp) ? tidProp.GetString() : null;

            var roles = new List<string>();
            if (root.TryGetProperty("roles", out var rolesProp) && rolesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rolesProp.EnumerateArray())
                    roles.Add(r.GetString() ?? string.Empty);
            }

            DateTimeOffset? expiry = null;
            if (root.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expUnix))
                expiry = DateTimeOffset.FromUnixTimeSeconds(expUnix);

            var hasExchangeManageAsApp = roles.Contains("Exchange.ManageAsApp", StringComparer.OrdinalIgnoreCase);

            _logger.LogInformation(
                "EXO token diagnostic for tenant {TenantId}: aud={Aud}, appid={AppId}, " +
                "roles=[{Roles}], hasExchangeManageAsApp={HasPerm}, expiry={Expiry}.",
                tenant.TenantId, aud, appid, string.Join(", ", roles), hasExchangeManageAsApp, expiry);

            return Ok(new
            {
                success = true,
                tenantId = tenant.TenantId,
                appClientId = tenant.AppClientId,
                token = new
                {
                    aud,
                    appid,
                    tid,
                    roles,
                    expiresAt = expiry,
                    hasExchangeManageAsApp,
                    diagnosis = hasExchangeManageAsApp
                        ? "Exchange.ManageAsApp is present — the token claims look correct. " +
                          "If EXO still returns 401, verify the EXO service principal is registered " +
                          "with New-ServicePrincipal and role assignments are linked to the SP Identity (not AppId)."
                        : "Exchange.ManageAsApp is MISSING from the roles claim. " +
                          "Grant the Exchange.ManageAsApp application permission in Azure AD and " +
                          "ensure admin consent has been granted in the SOURCE tenant.",
                },
            });
        }
    }

    /// <summary>
    /// Delete a tenant registration, including its Key Vault secrets when
    /// Key Vault is enabled.
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await _tenants.ExistsAsync(id, ct)) return NotFound();

        // Remove Key Vault secrets before deleting the DB record so we don't
        // leave orphaned secrets behind.
        await _keyVault.DeleteCredentialsAsync(id, ct);

        await _tenants.DeleteAsync(id, ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "TENANT_REMOVED",
            Resource = $"tenants/{id}",
            Actor = _currentUser.UserName,
        }, ct);
        await _audit.SaveAsync(ct);

        return NoContent();
    }
}
