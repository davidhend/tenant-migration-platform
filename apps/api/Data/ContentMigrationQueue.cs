namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-process, unbounded channel used to pass content migration job IDs from
/// controllers to the background <see cref="Workers.ContentMigrationWorker"/>.
/// This is intentionally kept in-memory (not persisted); on restart the worker
/// re-hydrates from the database for any Running jobs.
/// </summary>
public class ContentMigrationQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
}
