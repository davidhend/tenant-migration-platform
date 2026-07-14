namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-process, unbounded channel that passes OneDrive content-migration job IDs
/// from the controller to the background <see cref="Workers.OneDriveProvisioningWorker"/>.
/// The worker monitors target-tenant OneDrive provisioning and transitions the job
/// from <c>Provisioning</c> to <c>Ready</c> once all target UPNs have a drive.
/// </summary>
public class OneDriveProvisioningQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
}
