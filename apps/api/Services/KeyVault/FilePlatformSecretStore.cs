using System.Text.Json.Nodes;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// File-backed <see cref="IPlatformSecretStore"/> for setups without Key Vault
/// (KeyVault:Enabled=false). Secrets are written as plaintext values at their
/// config paths inside <c>settings.override.json</c> — exactly the
/// pre-Key-Vault behavior, kept for zero-friction local development. All file
/// access is serialized through <see cref="SettingsOverrideFile"/>.
/// </summary>
public sealed class FilePlatformSecretStore : IPlatformSecretStore
{
    private readonly string _path;
    private readonly ILogger<FilePlatformSecretStore> _logger;

    public bool IsExternal => false;

    public FilePlatformSecretStore(IWebHostEnvironment env, ILogger<FilePlatformSecretStore> logger)
    {
        _path = SettingsOverrideFile.GetPath(env);
        _logger = logger;
    }

    private static string ConfigPathFor(string name)
    {
        foreach (var (configPath, storeName) in PlatformSecretNames.ByConfigPath)
            if (string.Equals(storeName, name, StringComparison.OrdinalIgnoreCase))
                return configPath;
        throw new ArgumentException($"'{name}' is not a managed platform secret name.", nameof(name));
    }

    public async Task<string?> GetSecretAsync(string name, CancellationToken ct = default)
    {
        var root = await SettingsOverrideFile.ReadAsync(_path, ct);
        var parent = SettingsOverrideFile.EnsureParent(root, ConfigPathFor(name), out var leaf);
        var value = parent[leaf]?.GetValue<string>();
        return string.IsNullOrEmpty(value) || PlatformSecretNames.IsMarker(value) ? null : value;
    }

    public Task SetSecretAsync(string name, string value, CancellationToken ct = default) =>
        SettingsOverrideFile.UpdateAsync(_path, root =>
        {
            var parent = SettingsOverrideFile.EnsureParent(root, ConfigPathFor(name), out var leaf);
            parent[leaf] = value;
        }, ct);

    public Task DeleteSecretAsync(string name, CancellationToken ct = default) =>
        SettingsOverrideFile.UpdateAsync(_path, root =>
        {
            var parent = SettingsOverrideFile.EnsureParent(root, ConfigPathFor(name), out var leaf);
            parent.Remove(leaf);
        }, ct);
}
