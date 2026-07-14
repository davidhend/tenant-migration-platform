namespace MigrationPlatform.Api.Services.InstanceLock;

/// <summary>
/// Process-wide record of whether this instance won the single-instance
/// advisory lock and is therefore permitted to run background workers.
/// </summary>
public interface IInstanceRole
{
    /// <summary>
    /// True when this instance holds the single-instance lock (or enforcement is
    /// disabled / the database was unreachable at startup). Background workers
    /// suppress their processing when this is false.
    /// </summary>
    bool IsPrimary { get; }
}

/// <summary>
/// Backing state for <see cref="IInstanceRole"/>. A plain static flag is used so
/// background workers can consult it without a constructor/DI change; it is
/// written exactly once, by <see cref="SingleInstanceGuard"/> during host
/// startup (before any worker's ExecuteAsync runs), then only read.
/// </summary>
internal static class SingleInstanceState
{
    // Defaults to primary: if enforcement is off or the guard never runs, the
    // (single) instance must still process work.
    private static volatile bool _isPrimary = true;

    public static bool IsPrimary
    {
        get => _isPrimary;
        set => _isPrimary = value;
    }
}

/// <inheritdoc />
public sealed class InstanceRole : IInstanceRole
{
    public bool IsPrimary => SingleInstanceState.IsPrimary;
}
