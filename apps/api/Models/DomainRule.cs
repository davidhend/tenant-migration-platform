namespace MigrationPlatform.Api.Models;

/// <summary>
/// Describes how a domain transformation rule matches and rewrites a source UPN.
/// </summary>
public enum DomainRuleType
{
    /// <summary>Replaces the domain portion of a UPN. SourcePattern=source domain, TargetPattern=target domain.</summary>
    DirectMap,

    /// <summary>
    /// Domain swap with a distinct UI label indicating prefix-aware intent.
    /// Mechanically identical to <see cref="DirectMap"/> — only the domain portion is swapped.
    /// </summary>
    PrefixReplace,

    /// <summary>
    /// Full .NET regex substitution. SourcePattern=regex, TargetPattern=replacement string
    /// (supports capture-group back-references such as <c>$1</c>).
    /// </summary>
    RegexReplace,

    /// <summary>
    /// Explicit one-to-one override. SourcePattern=full source UPN, TargetPattern=full target UPN.
    /// Evaluated before domain-level rules for the same priority tier.
    /// </summary>
    FullUpnMap,
}

/// <summary>
/// A single domain transformation rule scoped to a migration project.
/// Rules are evaluated in ascending <see cref="Priority"/> order; the first matching
/// rule wins and subsequent rules are skipped for that UPN.
/// </summary>
public class DomainRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The project this rule belongs to.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Navigation property populated by EF Core Include() where needed.</summary>
    public MigrationProject? Project { get; set; }

    /// <summary>
    /// Evaluation order — lower value means higher priority.
    /// Rules with the same priority are evaluated in insertion order.
    /// </summary>
    public int Priority { get; set; }

    public DomainRuleType RuleType { get; set; }

    /// <summary>
    /// For DirectMap / PrefixReplace: the source domain (e.g. <c>contoso.com</c>).
    /// For RegexReplace: the .NET regex pattern.
    /// For FullUpnMap: the exact source UPN.
    /// </summary>
    public string SourcePattern { get; set; } = string.Empty;

    /// <summary>
    /// For DirectMap / PrefixReplace: the target domain (e.g. <c>fabrikam.com</c>).
    /// For RegexReplace: the replacement string (may contain <c>$1</c>, <c>$2</c> back-references).
    /// For FullUpnMap: the exact target UPN.
    /// </summary>
    public string TargetPattern { get; set; } = string.Empty;

    /// <summary>Disabled rules are loaded but skipped during transformation.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Optional human-readable description shown in the UI.</summary>
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
