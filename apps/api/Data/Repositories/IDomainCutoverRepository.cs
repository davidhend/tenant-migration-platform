using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Repositories;

public interface IDomainCutoverRepository
{
    Task<IEnumerable<DomainCutoverJob>> GetByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<DomainCutoverJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);
    Task<DomainCutoverJob?> GetWithProjectAsync(Guid jobId, CancellationToken ct = default);
    Task<IEnumerable<DomainCutoverJob>> GetActiveJobsAsync(CancellationToken ct = default);
    Task AddAsync(DomainCutoverJob job, CancellationToken ct = default);
    Task DeleteAsync(Guid jobId, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
