using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.DTOs;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/identity-maps")]
[Authorize]
public class IdentityMapsController : ControllerBase
{
    private readonly IIdentityMapRepository _maps;
    private readonly IProjectRepository _projects;
    private readonly IScanRepository _scans;
    private readonly IAuditRepository _audit;
    private readonly IDomainTransformService _transform;
    private readonly ICurrentUserService _currentUser;

    public IdentityMapsController(
        IIdentityMapRepository maps,
        IProjectRepository projects,
        IScanRepository scans,
        IAuditRepository audit,
        IDomainTransformService transform,
        ICurrentUserService currentUser)
    {
        _maps = maps;
        _projects = projects;
        _scans = scans;
        _audit = audit;
        _transform = transform;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct) =>
        Ok(await _maps.GetByProjectAsync(projectId, ct));

    [Authorize(Policy = "Operator")]
    [Authorize(Policy = "Operator")]
    [HttpPut("{mapId:guid}")]
    public async Task<IActionResult> Update(Guid projectId, Guid mapId, [FromBody] UpdateIdentityMapRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TargetUpn))
            return BadRequest("TargetUpn is required.");

        var map = await _maps.GetByIdAsync(mapId, ct);
        if (map is null) return NotFound();
        if (map.ProjectId != projectId) return NotFound();

        var previousUpn = map.TargetUpn;
        map.TargetUpn = req.TargetUpn.Trim();
        map.Status = MappingStatus.Mapped;
        map.MappingSource = MappingSource.Manual;
        map.ConflictReason = null;

        await _maps.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "IDENTITY_MAP_UPDATED",
            Resource = $"projects/{projectId}/identity-maps/{mapId}",
            Actor = _currentUser.UserName,
            ProjectId = projectId,
            Details = $$$"""{"sourceUpn":"{{{map.SourceUpn}}}","previousTargetUpn":"{{{previousUpn}}}","targetUpn":"{{{map.TargetUpn}}}"}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return Ok(map);
    }

    [Authorize(Policy = "Operator")]
    [HttpPost("auto-map")]
    public async Task<IActionResult> AutoMap(Guid projectId, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
        if (project is null) return NotFound();

        // Get users from the most recent completed source scan
        var sourceScan = await _scans.GetLatestCompletedAsync(project.SourceTenantId, ct);
        if (sourceScan is null)
            return BadRequest("No completed scan found for source tenant. Run a scan first.");

        // Simulate mapping latency
        await Task.Delay(1200, ct);

        var users = await _scans.GetUsersAsync(sourceScan.Id, ct);

        // Remove existing auto-generated maps for this project before regenerating
        await _maps.DeleteAutoMapsForProjectAsync(projectId, ct);

        // Apply the project's domain transformation rules to derive target UPNs.
        // Falls back to the source UPN unchanged when no matching rule exists.
        var sourceUpns = users.Select(u => u.Upn).ToList();
        var transformed = await _transform.TransformUpnBatchAsync(projectId, sourceUpns, ct);

        int mapped = 0, conflicts = 0, unmapped = 0;
        var newMaps = new List<IdentityMap>(users.Count);

        foreach (var user in users)
        {
            var targetUpn = transformed.GetValueOrDefault(user.Upn, user.Upn);
            var domainChanged = !string.Equals(targetUpn, user.Upn, StringComparison.OrdinalIgnoreCase);

            MappingStatus status;
            string? conflictReason = null;

            if (!domainChanged)
            {
                // No transformation rule matched — cannot determine target identity.
                status = MappingStatus.Unmapped;
                unmapped++;
            }
            else
            {
                status = MappingStatus.Mapped;
                mapped++;
            }

            newMaps.Add(new IdentityMap
            {
                ProjectId = projectId,
                SourceUpn = user.Upn,
                TargetUpn = targetUpn,
                Status = status,
                ConflictReason = conflictReason,
                MappingSource = MappingSource.Auto,
            });
        }

        await _maps.AddRangeAsync(newMaps, ct);
        await _maps.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "IDENTITY_MAP_AUTO_RUN",
            Resource = $"projects/{projectId}/identity-maps",
            Actor = _currentUser.UserName,
            ProjectId = projectId,
            Details = $$$"""{"mapped":{{{mapped}}},"conflicts":{{{conflicts}}},"unmapped":{{{unmapped}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return Ok(new AutoMapResult(mapped, conflicts, unmapped));
    }

    /// <summary>
    /// Applies the project's domain transformation rules to all currently unmapped identity maps,
    /// writing the transformed UPN as the target and marking the map as Mapped.
    /// Maps for which no rule matches are left in their current state (skipped).
    /// </summary>
    [Authorize(Policy = "Operator")]
    [HttpPost("apply-domain-rules")]
    public async Task<IActionResult> ApplyDomainRules(Guid projectId, CancellationToken ct)
    {
        var project = await _projects.GetByIdWithTenantsAsync(projectId, ct);
        if (project is null) return NotFound();

        // Load only unmapped entries — already-mapped and conflict entries are not touched
        var allMaps = await _maps.GetByProjectAsync(projectId, ct);
        var unmapped = allMaps.Where(m => m.Status == MappingStatus.Unmapped).ToList();

        if (unmapped.Count == 0)
            return Ok(new { applied = 0, skipped = 0 });

        var sourceUpns = unmapped.Select(m => m.SourceUpn);
        var transformed = await _transform.TransformUpnBatchAsync(projectId, sourceUpns, ct);

        int applied = 0;
        int skipped = 0;

        foreach (var map in unmapped)
        {
            if (!transformed.TryGetValue(map.SourceUpn, out var targetUpn))
            {
                skipped++;
                continue;
            }

            // A transformation is only considered a match if the result differs from the source
            if (string.Equals(targetUpn, map.SourceUpn, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            map.TargetUpn = targetUpn;
            map.Status = MappingStatus.Mapped;
            map.MappingSource = MappingSource.Auto;
            map.ConflictReason = null;
            applied++;
        }

        await _maps.SaveAsync(ct);

        await _audit.AddAsync(new AuditEvent
        {
            Action = "DOMAIN_RULES_APPLIED",
            Resource = $"projects/{projectId}/identity-maps",
            Actor = _currentUser.UserName,
            ProjectId = projectId,
            Details = $$$"""{"applied":{{{applied}}},"skipped":{{{skipped}}}}""",
        }, ct);
        await _audit.SaveAsync(ct);

        return Ok(new { applied, skipped });
    }
}
