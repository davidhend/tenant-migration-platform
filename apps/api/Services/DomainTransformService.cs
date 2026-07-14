using System.Text.RegularExpressions;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services;

/// <summary>
/// Transforms source UPNs to target UPNs by evaluating the project's
/// <see cref="DomainRule"/> set in priority order.
/// </summary>
public interface IDomainTransformService
{
    /// <summary>
    /// Applies the highest-priority matching rule from the project's rule set to transform a UPN.
    /// Returns the transformed UPN, or the original if no rule matches.
    /// </summary>
    Task<string> TransformUpnAsync(Guid projectId, string sourceUpn, CancellationToken ct = default);

    /// <summary>
    /// Applies transformation to a batch of UPNs.
    /// Returns a dictionary of source UPN → target UPN (unchanged entries map to themselves).
    /// </summary>
    Task<Dictionary<string, string>> TransformUpnBatchAsync(
        Guid projectId,
        IEnumerable<string> sourceUpns,
        CancellationToken ct = default);

    /// <summary>
    /// Previews what the current rule set would produce for a list of sample UPNs
    /// without persisting any changes.
    /// </summary>
    Task<List<TransformPreviewResult>> PreviewTransformAsync(
        Guid projectId,
        IEnumerable<string> sampleUpns,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a single UPN transformation preview.
/// </summary>
/// <param name="SourceUpn">The original UPN.</param>
/// <param name="TargetUpn">The transformed UPN (equals <paramref name="SourceUpn"/> when no rule matched).</param>
/// <param name="Matched">True if a rule was applied; false if the UPN passes through unchanged.</param>
/// <param name="RuleDescription">Human-readable description of the matched rule, or null.</param>
public record TransformPreviewResult(
    string SourceUpn,
    string TargetUpn,
    bool Matched,
    string? RuleDescription);

/// <summary>
/// Scoped implementation of <see cref="IDomainTransformService"/>.
/// Rules for a project are loaded once per service instance (i.e., once per request scope)
/// and cached in a private dictionary for the lifetime of the scope.
/// </summary>
public sealed class DomainTransformService : IDomainTransformService
{
    private readonly IDomainRuleRepository _rules;
    private readonly ILogger<DomainTransformService> _logger;

    // Per-scope cache: projectId → ordered enabled rules
    private readonly Dictionary<Guid, List<DomainRule>> _ruleCache = new();

    public DomainTransformService(IDomainRuleRepository rules, ILogger<DomainTransformService> logger)
    {
        _rules = rules;
        _logger = logger;
    }

    public async Task<string> TransformUpnAsync(Guid projectId, string sourceUpn, CancellationToken ct = default)
    {
        var rules = await GetRulesAsync(projectId, ct);
        var (transformed, _) = Apply(rules, sourceUpn);
        return transformed;
    }

    public async Task<Dictionary<string, string>> TransformUpnBatchAsync(
        Guid projectId,
        IEnumerable<string> sourceUpns,
        CancellationToken ct = default)
    {
        var rules = await GetRulesAsync(projectId, ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var upn in sourceUpns)
        {
            var (transformed, _) = Apply(rules, upn);
            result[upn] = transformed;
        }

        return result;
    }

    public async Task<List<TransformPreviewResult>> PreviewTransformAsync(
        Guid projectId,
        IEnumerable<string> sampleUpns,
        CancellationToken ct = default)
    {
        var rules = await GetRulesAsync(projectId, ct);
        var results = new List<TransformPreviewResult>();

        foreach (var upn in sampleUpns)
        {
            var (transformed, matchedRule) = Apply(rules, upn);
            results.Add(new TransformPreviewResult(
                SourceUpn: upn,
                TargetUpn: transformed,
                Matched: matchedRule is not null,
                RuleDescription: matchedRule?.Description ?? (matchedRule is not null
                    ? $"{matchedRule.RuleType}: {matchedRule.SourcePattern} → {matchedRule.TargetPattern}"
                    : null)));
        }

        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads and caches the enabled rules for a project, ordered by Priority ascending.
    /// Subsequent calls within the same scope return the cached list.
    /// </summary>
    private async Task<List<DomainRule>> GetRulesAsync(Guid projectId, CancellationToken ct)
    {
        if (_ruleCache.TryGetValue(projectId, out var cached))
            return cached;

        var all = await _rules.GetByProjectAsync(projectId, ct);
        var enabled = all.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

        _ruleCache[projectId] = enabled;
        return enabled;
    }

    /// <summary>
    /// Iterates rules in priority order and applies the first matching rule to
    /// <paramref name="upn"/>. Returns the (possibly transformed) UPN and the
    /// rule that matched, or (original UPN, null) when no rule matches.
    /// </summary>
    private (string TransformedUpn, DomainRule? MatchedRule) Apply(List<DomainRule> rules, string upn)
    {
        foreach (var rule in rules)
        {
            var result = TryApplyRule(rule, upn);
            if (result is not null)
                return (result, rule);
        }

        return (upn, null);
    }

    /// <summary>
    /// Attempts to apply a single rule to a UPN. Returns the transformed string on
    /// match, or null if the rule does not match.
    /// </summary>
    private string? TryApplyRule(DomainRule rule, string upn)
    {
        return rule.RuleType switch
        {
            DomainRuleType.DirectMap or DomainRuleType.PrefixReplace => ApplyDomainSwap(rule, upn),
            DomainRuleType.RegexReplace => ApplyRegex(rule, upn),
            DomainRuleType.FullUpnMap => ApplyFullUpnMap(rule, upn),
            _ => null,
        };
    }

    private static string? ApplyDomainSwap(DomainRule rule, string upn)
    {
        var suffix = "@" + rule.SourcePattern;
        if (!upn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var localPart = upn[..^suffix.Length];
        return $"{localPart}@{rule.TargetPattern}";
    }

    private string? ApplyRegex(DomainRule rule, string upn)
    {
        try
        {
            if (!Regex.IsMatch(upn, rule.SourcePattern, RegexOptions.IgnoreCase))
                return null;

            return Regex.Replace(upn, rule.SourcePattern, rule.TargetPattern, RegexOptions.IgnoreCase);
        }
        catch (RegexParseException ex)
        {
            _logger.LogWarning(ex,
                "DomainTransformService: skipping rule {RuleId} — invalid regex pattern '{Pattern}'.",
                rule.Id, rule.SourcePattern);
            return null;
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "DomainTransformService: skipping rule {RuleId} — regex evaluation timed out for UPN '{Upn}'.",
                rule.Id, upn);
            return null;
        }
    }

    private static string? ApplyFullUpnMap(DomainRule rule, string upn)
    {
        if (!upn.Equals(rule.SourcePattern, StringComparison.OrdinalIgnoreCase))
            return null;

        return rule.TargetPattern;
    }
}
