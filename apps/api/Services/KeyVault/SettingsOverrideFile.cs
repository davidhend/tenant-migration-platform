using System.Text.Json;
using System.Text.Json.Nodes;

namespace MigrationPlatform.Api.Services.KeyVault;

/// <summary>
/// Serialized access to <c>settings.override.json</c> (the runtime-writable
/// config layer). All writers — SettingsController, the file-backed secret
/// store, and the startup secret migrator — go through this helper so
/// concurrent read-modify-write cycles cannot interleave and clobber each
/// other's keys.
/// </summary>
public static class SettingsOverrideFile
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string GetPath(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "settings.override.json");

    /// <summary>Run <paramref name="mutate"/> against the parsed file under the global lock and persist the result.</summary>
    public static async Task UpdateAsync(string path, Action<JsonObject> mutate, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var root = Load(path);
            mutate(root);
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Read the parsed file under the global lock (empty object when absent/malformed).</summary>
    public static async Task<JsonObject> ReadAsync(string path, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return Load(path);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            // Malformed file — start from scratch rather than crash settings writes.
            return new JsonObject();
        }
    }

    /// <summary>Navigate (creating as needed) to the parent object of a colon-separated config path.</summary>
    public static JsonObject EnsureParent(JsonObject root, string configPath, out string leafKey)
    {
        var segments = configPath.Split(':');
        leafKey = segments[^1];
        var node = root;
        foreach (var segment in segments[..^1])
        {
            if (node[segment] is not JsonObject child)
            {
                child = new JsonObject();
                node[segment] = child;
            }
            node = child;
        }
        return node;
    }
}
