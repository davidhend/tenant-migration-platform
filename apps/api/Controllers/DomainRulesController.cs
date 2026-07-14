using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

// ── Request / response records ────────────────────────────────────────────────

public record CreateDomainRuleRequest(
    DomainRuleType RuleType,
    string SourcePattern,
    string TargetPattern,
    int Priority,
    bool IsEnabled,
    string? Description);

public record UpdateDomainRuleRequest(
    string? SourcePattern,
    string? TargetPattern,
    int? Priority,
    bool? IsEnabled,
    string? Description);

public record PreviewRequest(List<string> SampleUpns);

// ── Controller ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/projects/{projectId:guid}/domain-rules")]
[Authorize]
public class DomainRulesController : ControllerBase
{
    private readonly IDomainRuleRepository _rules;
    private readonly IProjectRepository _projects;
    private readonly IDomainTransformService _transform;
    private readonly ILogger<DomainRulesController> _logger;

    public DomainRulesController(
        IDomainRuleRepository rules,
        IProjectRepository projects,
        IDomainTransformService transform,
        ILogger<DomainRulesController> logger)
    {
        _rules = rules;
        _projects = projects;
        _transform = transform;
        _logger = logger;
    }

    /// <summary>Returns all domain transformation rules for a project, ordered by Priority.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid projectId, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct))
            return NotFound();

        return Ok(await _rules.GetByProjectAsync(projectId, ct));
    }

    /// <summary>Creates a new domain transformation rule for a project.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPost]
    public async Task<IActionResult> Create(Guid projectId, [FromBody] CreateDomainRuleRequest req, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct))
            return NotFound();

        var rule = new DomainRule
        {
            ProjectId = projectId,
            RuleType = req.RuleType,
            SourcePattern = req.SourcePattern,
            TargetPattern = req.TargetPattern,
            Priority = req.Priority,
            IsEnabled = req.IsEnabled,
            Description = req.Description,
        };

        await _rules.AddAsync(rule, ct);
        await _rules.SaveAsync(ct);

        _logger.LogInformation(
            "DomainRulesController: created rule {RuleId} ({RuleType}) for project {ProjectId}.",
            rule.Id, rule.RuleType, projectId);

        return CreatedAtAction(nameof(GetById), new { projectId, ruleId = rule.Id }, rule);
    }

    /// <summary>Returns a single domain rule by ID.</summary>
    [HttpGet("{ruleId:guid}")]
    public async Task<IActionResult> GetById(Guid projectId, Guid ruleId, CancellationToken ct)
    {
        var rule = await _rules.GetByIdAsync(ruleId, ct);
        if (rule is null || rule.ProjectId != projectId) return NotFound();
        return Ok(rule);
    }

    /// <summary>Updates an existing domain rule. Only supplied fields are changed.</summary>
    [Authorize(Policy = "Operator")]
    [HttpPut("{ruleId:guid}")]
    public async Task<IActionResult> Update(
        Guid projectId,
        Guid ruleId,
        [FromBody] UpdateDomainRuleRequest req,
        CancellationToken ct)
    {
        var rule = await _rules.GetByIdAsync(ruleId, ct);
        if (rule is null || rule.ProjectId != projectId) return NotFound();

        if (req.SourcePattern is not null) rule.SourcePattern = req.SourcePattern;
        if (req.TargetPattern is not null) rule.TargetPattern = req.TargetPattern;
        if (req.Priority is not null)      rule.Priority      = req.Priority.Value;
        if (req.IsEnabled is not null)     rule.IsEnabled     = req.IsEnabled.Value;
        if (req.Description is not null)   rule.Description   = req.Description;

        await _rules.SaveAsync(ct);

        _logger.LogInformation(
            "DomainRulesController: updated rule {RuleId} for project {ProjectId}.",
            ruleId, projectId);

        return Ok(rule);
    }

    /// <summary>Deletes a domain rule.</summary>
    [Authorize(Policy = "Operator")]
    [HttpDelete("{ruleId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid ruleId, CancellationToken ct)
    {
        var rule = await _rules.GetByIdAsync(ruleId, ct);
        if (rule is null || rule.ProjectId != projectId) return NotFound();

        await _rules.DeleteAsync(ruleId, ct);

        _logger.LogInformation(
            "DomainRulesController: deleted rule {RuleId} from project {ProjectId}.",
            ruleId, projectId);

        return NoContent();
    }

    /// <summary>
    /// Previews the transformation output for a set of sample UPNs without persisting anything.
    /// Useful for testing rules before applying them to identity maps.
    /// </summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(Guid projectId, [FromBody] PreviewRequest req, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct))
            return NotFound();

        if (req.SampleUpns is null || req.SampleUpns.Count == 0)
            return BadRequest("SampleUpns must contain at least one UPN.");

        var results = await _transform.PreviewTransformAsync(projectId, req.SampleUpns, ct);
        return Ok(results);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        await _projects.GetByIdWithTenantsAsync(projectId, ct) is not null;
}
