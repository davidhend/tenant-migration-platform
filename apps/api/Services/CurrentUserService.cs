using System.Security.Claims;

namespace MigrationPlatform.Api.Services;

/// <inheritdoc />
public sealed class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string UserName
    {
        get
        {
            var p = Principal;
            if (p?.Identity?.IsAuthenticated != true) return "system@platform";

            // Entra ID v2 tokens carry preferred_username (UPN); fall back through
            // the common name claims, then the dev token's sub.
            return p.FindFirstValue("preferred_username")
                ?? p.FindFirstValue(ClaimTypes.Upn)
                ?? p.FindFirstValue(ClaimTypes.Email)
                ?? p.FindFirstValue(ClaimTypes.Name)
                ?? p.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? p.FindFirstValue("sub")
                ?? "unknown@platform";
        }
    }

    public IReadOnlyList<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Concat(Principal?.FindAll("roles").Select(c => c.Value) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}
