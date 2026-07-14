namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-process, unbounded channel used to pass user migration batch IDs from
/// controllers to the background <see cref="Workers.UserMigrationWorker"/>.
/// Kept in-memory only; on restart the worker re-hydrates from the database
/// for any batches still in <see cref="Models.UserMigrationBatchStatus.Provisioning"/>.
/// </summary>
public class UserMigrationQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
}
