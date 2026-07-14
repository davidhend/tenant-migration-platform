namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-process, unbounded channel used to pass validation run IDs from controllers
/// to the background <see cref="Workers.ValidationWorker"/>.
/// On restart the worker re-hydrates from the database for any Pending/Running runs.
/// </summary>
public class ValidationQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
}
