using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Authentication entry points. GET /config tells the frontend which sign-in
/// mode the API runs in (Entra ID when the AzureAd section is configured, the
/// local dev credential flow when Platform:DevMode=true, otherwise none).
/// POST /token issues local dev JWTs and is disabled outside dev mode —
/// production tokens come from Entra ID directly.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    // Local-scheme default account — usable only when Platform:DevMode=true.
    // The well-known default password is seeded into the LocalCredentials table
    // (hashed) on first login with MustChangePassword=true; operators are then
    // prompted to rotate it via POST /api/auth/change-password. Override the
    // seeded value with Auth:LocalAdmin:InitialPassword (e.g. via env var) for
    // deployments that never want the well-known default to be valid at all.
    private const string DevUsername = "admin";
    private const string DefaultDevPassword = "MigrationAdmin123!";
    private const string AdminRole = "Admin";
    private const int MinPasswordLength = 12;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);
    private static readonly PasswordHasher<LocalCredential> Hasher = new();

    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly AppDbContext _db;

    public AuthController(IConfiguration config, ILogger<AuthController> logger, AppDbContext db)
    {
        _config = config;
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// Exchange credentials for a JWT bearer token.
    /// In dev mode accepts username=admin / password=MigrationAdmin123!.
    /// </summary>
    /// <remarks>
    /// Returns a signed JWT valid for 8 hours. Include it in subsequent requests as:
    /// <c>Authorization: Bearer &lt;token&gt;</c>
    /// </remarks>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> Token([FromBody] TokenRequest req, CancellationToken ct)
    {
        bool devMode = _config.GetValue<bool>("Platform:DevMode");

        if (!devMode)
        {
            // Production: this endpoint should not be used — Entra ID issues tokens.
            _logger.LogWarning("Token endpoint called in non-dev mode; rejecting.");
            return StatusCode(StatusCodes.Status501NotImplemented,
                new { error = "Direct credential issuance is disabled in production. Use Azure AD / Entra ID." });
        }

        var cred = await _db.LocalCredentials.SingleOrDefaultAsync(c => c.UserName == req.Username, ct);

        if (cred is null && string.Equals(req.Username, DevUsername, StringComparison.Ordinal))
        {
            // First local login ever: seed the default admin credential (hashed).
            var initialPassword = _config["Auth:LocalAdmin:InitialPassword"];
            var seededFromConfig = !string.IsNullOrWhiteSpace(initialPassword);
            cred = new LocalCredential
            {
                Id                 = Guid.NewGuid(),
                UserName           = DevUsername,
                MustChangePassword = !seededFromConfig,
                CreatedAt          = DateTime.UtcNow,
            };
            cred.PasswordHash = Hasher.HashPassword(cred, seededFromConfig ? initialPassword! : DefaultDevPassword);
            _db.LocalCredentials.Add(cred);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Seeded local admin credential ({Source}).",
                seededFromConfig ? "from Auth:LocalAdmin:InitialPassword" : "well-known default; change prompt armed");
        }

        if (cred is null)
        {
            _logger.LogWarning("Failed auth attempt for unknown username '{Username}'.", req.Username);
            return Unauthorized(new { error = "Invalid credentials." });
        }

        var verdict = Hasher.VerifyHashedPassword(cred, cred.PasswordHash, req.Password ?? "");
        if (verdict == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Failed auth attempt for username '{Username}'.", req.Username);
            return Unauthorized(new { error = "Invalid credentials." });
        }
        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            cred.PasswordHash = Hasher.HashPassword(cred, req.Password!);
            await _db.SaveChangesAsync(ct);
        }

        var token = BuildToken(req.Username) with { MustChangePassword = cred.MustChangePassword };
        _logger.LogInformation("Local token issued for '{Username}', expires {ExpiresAt}.", req.Username, token.ExpiresAt);

        return Ok(token);
    }

    /// <summary>
    /// Change the local account's password (local auth scheme only — Entra
    /// users manage their password in Entra ID). Clears the first-login
    /// must-change flag.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest req,
        [FromServices] ICurrentUserService currentUser,
        [FromServices] IAuditRepository audit,
        CancellationToken ct)
    {
        if (!_config.GetValue<bool>("Platform:DevMode"))
            return BadRequest(new { error = "Local password change is only available when the local auth scheme is active." });

        var cred = await _db.LocalCredentials.SingleOrDefaultAsync(c => c.UserName == currentUser.UserName, ct);
        if (cred is null)
            return BadRequest(new { error = "No local credential exists for this account (Entra-signed-in users manage their password in Entra ID)." });

        if (Hasher.VerifyHashedPassword(cred, cred.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Password change rejected for '{Username}': current password mismatch.", cred.UserName);
            return Unauthorized(new { error = "Current password is incorrect." });
        }

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < MinPasswordLength)
            return BadRequest(new { error = $"New password must be at least {MinPasswordLength} characters." });
        if (string.Equals(req.NewPassword, DefaultDevPassword, StringComparison.Ordinal))
            return BadRequest(new { error = "New password must not be the well-known default." });

        cred.PasswordHash       = Hasher.HashPassword(cred, req.NewPassword);
        cred.MustChangePassword = false;
        cred.PasswordChangedAt  = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await audit.AddAsync(new AuditEvent
        {
            Action   = "AUTH_LOCAL_PASSWORD_CHANGED",
            Resource = $"auth/local/{cred.UserName}",
            Actor    = currentUser.UserName,
            Details  = $$$"""{"userName":"{{{cred.UserName}}}"}""",
        }, ct);
        await audit.SaveAsync(ct);

        _logger.LogInformation("Local password changed for '{Username}'.", cred.UserName);
        return Ok(new { message = "Password changed." });
    }

    /// <summary>
    /// Public (unauthenticated) description of how to sign in, consumed by the
    /// frontend at startup. Never returns secrets.
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthConfigResponse), StatusCodes.Status200OK)]
    public IActionResult GetConfig()
    {
        var tenantId = _config["AzureAd:TenantId"];
        var clientId = _config["AzureAd:ClientId"];
        var entraConfigured = !string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId);
        var devMode = _config.GetValue<bool>("Platform:DevMode");

        var mode = entraConfigured ? "entraId" : devMode ? "local" : "none";

        return Ok(new AuthConfigResponse(
            Mode:     mode,
            TenantId: entraConfigured ? tenantId : null,
            ClientId: entraConfigured ? clientId : null,
            ApiScope: entraConfigured ? $"api://{clientId}/access_as_user" : null));
    }

    /// <summary>Identity and roles of the calling principal.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Me([FromServices] ICurrentUserService currentUser) =>
        Ok(new
        {
            userName      = currentUser.UserName,
            roles         = currentUser.Roles,
            authenticated = true,
        });

    // ── Private helpers ──────────────────────────────────────────────────────

    private TokenResponse BuildToken(string username)
    {
        var secretKey = _config["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey is not configured.");
        var issuer    = _config["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
        var audience  = _config["Jwt:Audience"]
            ?? throw new InvalidOperationException("Jwt:Audience is not configured.");

        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now       = DateTime.UtcNow;
        var expiresAt = now.Add(TokenLifetime);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(ClaimTypes.Role, AdminRole),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };

        var jwt = new JwtSecurityToken(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            notBefore:          now,
            expires:            expiresAt,
            signingCredentials: creds);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(jwt);
        return new TokenResponse(tokenString, expiresAt);
    }
}

// ── Inline DTOs (simple enough to live alongside the controller) ─────────────

/// <summary>Credential payload for <c>POST /api/auth/token</c>.</summary>
public sealed record TokenRequest(string Username, string Password);

/// <summary>
/// Successful token response payload. <see cref="MustChangePassword"/> is true
/// when the account still uses the seeded default password — the frontend
/// prompts for a change at sign-in.
/// </summary>
public sealed record TokenResponse(string Token, DateTime ExpiresAt, bool MustChangePassword = false);

/// <summary>Payload for <c>POST /api/auth/change-password</c> (local scheme only).</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Sign-in configuration exposed to the frontend (no secrets).</summary>
public sealed record AuthConfigResponse(string Mode, string? TenantId, string? ClientId, string? ApiScope);
