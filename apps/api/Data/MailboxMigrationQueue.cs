namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-process, unbounded channel used to pass mailbox migration batch IDs from
/// controllers to the background <see cref="Workers.MailboxMigrationWorker"/>.
/// This is intentionally kept in-memory (not persisted); on restart the worker
/// re-hydrates from the database for any active (Syncing, Synced, or Completing) batches.
/// </summary>
public class MailboxMigrationQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
}
