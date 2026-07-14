using System.Collections.Concurrent;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Unified read path for platform secrets. Resolution precedence for a config
/// path like <c>Platform:CrossTenantMigration:ClientSecret</c>:
/// <list type="number">
///   <item>A REAL value in <see cref="IConfiguration"/> (environment variable
///   or appsettings — the dev/dev-ops override) wins. A <c>kv:</c> marker is
///   not a real value.</item>
///   <item>Otherwise the platform secret store (Key Vault in production, the
///   override file in dev).</item>
/// </list>
/// Store lookups are cached for a short TTL so hot paths (per-request ARM
/// token builds, batch starts) don't hit Key Vault every call. Writers must
/// call <see cref="Invalidate"/> after changing a secret.
/// </summary>
public interface IPlatformSecretResolver
{
    /// <summary>Resolve the effective secret value for a managed config path; null when unset everywhere.</summary>
    Task<string?> GetAsync(string configPath, CancellationToken ct = default);

    /// <summary>Drop the cached store value for a config path (call after Set/Delete).</summary>
    void Invalidate(string configPath);
}

/// <inheritdoc />
public sealed class PlatformSecretResolver : IPlatformSecretResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly IPlatformSecretStore _store;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (string? Value, DateTimeOffset At)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PlatformSecretResolver(
        IConfiguration configuration,
        IPlatformSecretStore store,
        TimeProvider? time = null)
    {
        _configuration = configuration;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string?> GetAsync(string configPath, CancellationToken ct = default)
    {
        // 1. Real config value wins (env var / appsettings dev override).
        var configValue = _configuration[configPath];
        if (!string.IsNullOrWhiteSpace(configValue) && !PlatformSecretNames.IsMarker(configValue))
            return configValue;

        // 2. Secret store, cached briefly.
        if (_cache.TryGetValue(configPath, out var hit) &&
            _time.GetUtcNow() - hit.At < CacheTtl)
            return hit.Value;

        var value = await _store.GetSecretAsync(PlatformSecretNames.ForConfigPath(configPath), ct);
        _cache[configPath] = (value, _time.GetUtcNow());
        return value;
    }

    public void Invalidate(string configPath) => _cache.TryRemove(configPath, out _);
}
