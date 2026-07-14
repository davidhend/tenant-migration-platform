using System.Text.Json.Nodes;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// One-shot startup migration: when the platform secret store is external
/// (Key Vault), any plaintext secret still sitting in
/// <c>settings.override.json</c> (written by pre-Key-Vault versions of the
/// Settings UI) is pushed into the store and replaced in the file with a
/// <c>kv:{name}</c> marker. The plaintext is only removed AFTER the store
/// write succeeds; store failures leave the file untouched so nothing is lost.
/// </summary>
public sealed class PlatformSecretMigrator : IHostedService
{
    private readonly IPlatformSecretStore _store;
    private readonly IPlatformSecretResolver _resolver;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PlatformSecretMigrator> _logger;

    public PlatformSecretMigrator(
        IPlatformSecretStore store,
        IPlatformSecretResolver resolver,
        IWebHostEnvironment env,
        ILogger<PlatformSecretMigrator> logger)
    {
        _store = store;
        _resolver = resolver;
        _env = env;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_store.IsExternal)
            return; // File mode — plaintext in the override file IS the store.

        var path = SettingsOverrideFile.GetPath(_env);
        if (!File.Exists(path))
            return;

        try
        {
            var root = await SettingsOverrideFile.ReadAsync(path, cancellationToken);

            // Collect plaintext values first (no file mutation yet).
            var pending = new List<(string ConfigPath, string StoreName, string Value)>();
            foreach (var (configPath, storeName) in PlatformSecretNames.ByConfigPath)
            {
                var parent = SettingsOverrideFile.EnsureParent(root, configPath, out var leaf);
                var value = parent[leaf]?.GetValue<string>();
                if (!string.IsNullOrEmpty(value) && !PlatformSecretNames.IsMarker(value))
                    pending.Add((configPath, storeName, value));
            }

            if (pending.Count == 0)
                return;

            // Push to the store; only successfully stored secrets get markers.
            var migrated = new List<(string ConfigPath, string StoreName)>();
            foreach (var (configPath, storeName, value) in pending)
            {
                try
                {
                    await _store.SetSecretAsync(storeName, value, cancellationToken);
                    migrated.Add((configPath, storeName));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Could not migrate plaintext secret at '{ConfigPath}' to Key Vault — " +
                        "leaving it in settings.override.json for now (will retry next startup).",
                        configPath);
                }
            }

            if (migrated.Count == 0)
                return;

            await SettingsOverrideFile.UpdateAsync(path, freshRoot =>
            {
                foreach (var (configPath, storeName) in migrated)
                {
                    var parent = SettingsOverrideFile.EnsureParent(freshRoot, configPath, out var leaf);
                    parent[leaf] = PlatformSecretNames.Marker + storeName;
                }
            }, cancellationToken);

            foreach (var (configPath, storeName) in migrated)
            {
                _resolver.Invalidate(configPath);
                _logger.LogInformation(
                    "Migrated plaintext secret '{ConfigPath}' from settings.override.json to Key Vault " +
                    "as '{StoreName}' (file now holds a kv: marker).",
                    configPath, storeName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Platform secret migration failed — plaintext secrets may remain in settings.override.json. " +
                "The app continues; migration retries on next startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
