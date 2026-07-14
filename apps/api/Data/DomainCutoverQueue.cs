namespace MigrationPlatform.Api.Data;

/// <summary>
/// In-memory queue for signalling the <see cref="Workers.DomainCutoverWorker"/>
/// that a domain cutover job needs processing.
/// </summary>
public class DomainCutoverQueue
{
    public System.Threading.Channels.Channel<Guid> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();
}
