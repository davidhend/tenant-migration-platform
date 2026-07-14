using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Discovery;

public interface IDiscoveryEngine
{
    Task<Scan> RunScanAsync(Guid scanId, CancellationToken cancellationToken = default);
}
