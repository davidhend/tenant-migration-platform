using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data.Repositories;

namespace MigrationPlatform.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly IAuditRepository _audit;

    public AuditController(IAuditRepository audit) => _audit = audit;

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? projectId = null,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _audit.GetPagedAsync(page, pageSize, projectId, ct);
        return Ok(new { items, totalCount, page, pageSize });
    }
}
