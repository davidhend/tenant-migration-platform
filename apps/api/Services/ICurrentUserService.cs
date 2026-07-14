namespace MigrationPlatform.Api.Services;

/// <summary>
/// Resolves the identity of the caller for audit attribution and
/// authorization decisions. Backed by the current HTTP context: Entra ID
/// tokens yield the user's UPN/preferred_username; local dev tokens yield the
/// dev username. Background/worker code (no HTTP context) resolves to
/// <c>system@platform</c>.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>Stable identifier for audit records (UPN, or dev username, or system@platform).</summary>
    string UserName { get; }

    /// <summary>True when an authenticated principal is present on the request.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Role claims of the current principal (empty when unauthenticated).</summary>
    IReadOnlyList<string> Roles { get; }
}
