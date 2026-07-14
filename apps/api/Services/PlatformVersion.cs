using System.Reflection;

namespace MigrationPlatform.Api.Services;

/// <summary>
/// The running platform version, read from the API assembly's informational
/// version (set from the repo-root VERSION file via Directory.Build.props).
/// The SPO runbook ships with the same version and carries a matching
/// <c>RUNBOOK_VERSION</c> marker, so <see cref="RunbookVersion"/> is the version
/// the API expects the deployed Automation runbook to be.
/// </summary>
public static class PlatformVersion
{
    /// <summary>Semantic version of the running platform, e.g. "0.9.0".</summary>
    public static string Current { get; } =
        (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
         ?? "0.0.0")
        // Defensive: strip any "+<metadata>" the SDK may append.
        .Split('+')[0];

    /// <summary>
    /// Version the API expects the deployed SPO runbook to be. The runbook is
    /// released in lockstep with the API, so this is the platform version.
    /// </summary>
    public static string RunbookVersion => Current;
}
