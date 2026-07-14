namespace MigrationPlatform.Api.Models;

/// <summary>
/// Local (non-Entra) sign-in credential for the dev/fallback auth scheme.
/// Only the password hash is stored (ASP.NET Identity PasswordHasher, PBKDF2).
/// The well-known default account is seeded on first login with
/// <see cref="MustChangePassword"/> = true so operators are prompted to rotate
/// it; the flag clears when the password is changed via /api/auth/change-password.
/// </summary>
public class LocalCredential
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
}
