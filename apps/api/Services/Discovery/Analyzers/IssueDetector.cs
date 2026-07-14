using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Discovery.Analyzers;

/// <summary>
/// Analyzes scan results and generates actionable ScanIssue records.
/// </summary>
public class IssueDetector
{
    public List<ScanIssue> Detect(
        Guid scanId,
        List<ScannedUser> users,
        List<ScannedMailbox> mailboxes,
        List<ScannedSite> sites,
        List<ScannedDomain> domains,
        IEnumerable<DomainRule>? projectRules = null)
    {
        var issues = new List<ScanIssue>();
        var rules = (projectRules ?? []).Where(r => r.IsEnabled).ToList();

        // BLOCKER: primary domain not in target — suppressed when a transformation rule covers the domain
        var primaryDomain = domains.FirstOrDefault(d => d.IsDefault);
        if (primaryDomain != null)
        {
            var coveredByRule = rules.Any(r =>
                (r.RuleType == DomainRuleType.DirectMap || r.RuleType == DomainRuleType.PrefixReplace) &&
                r.SourcePattern.Equals(primaryDomain.Name, StringComparison.OrdinalIgnoreCase));

            if (!coveredByRule)
            {
                issues.Add(new ScanIssue
                {
                    ScanId = scanId,
                    Severity = IssueSeverity.Blocker,
                    Category = "domain",
                    Code = "DOMAIN_NOT_IN_TARGET",
                    Title = "Primary domain not present in target tenant",
                    Description = $"The domain {primaryDomain.Name} is used by {primaryDomain.UserCount} users as their primary UPN but has not been added and verified in the target tenant. Mailbox migration will fail without this domain or a transformation rule.",
                    AffectedObjectCount = primaryDomain.UserCount,
                    RemediationSteps =
                    [
                        $"Add {primaryDomain.Name} to the target tenant via Microsoft 365 admin center.",
                        "Complete DNS verification for the domain in the target tenant.",
                        "Alternatively, configure a domain transformation rule to rewrite UPNs to the target domain.",
                    ],
                });
            }
        }

        // WARNING: large mailboxes
        var largeMailboxes = mailboxes.Where(m => m.SizeGb > 50).ToList();
        if (largeMailboxes.Count > 0)
        {
            issues.Add(new ScanIssue
            {
                ScanId = scanId,
                Severity = IssueSeverity.Warning,
                Category = "mailbox",
                Code = "LARGE_MAILBOX",
                Title = "Mailboxes exceeding 50 GB detected",
                Description = $"{largeMailboxes.Count} mailbox(es) exceed 50 GB and may require additional migration time or a pre-stage wave.",
                AffectedObjectCount = largeMailboxes.Count,
                RemediationSteps =
                [
                    "Consider running a pre-stage migration for large mailboxes during a maintenance window.",
                    "Ensure the target Exchange Online licenses include the required storage quota.",
                ],
            });
        }

        // WARNING: sites with unique permissions
        var uniquePermSites = sites.Where(s => s.HasUniquePermissions).ToList();
        if (uniquePermSites.Count > 0)
        {
            issues.Add(new ScanIssue
            {
                ScanId = scanId,
                Severity = IssueSeverity.Warning,
                Category = "sharepoint",
                Code = "SHAREPOINT_UNIQUE_PERMISSIONS",
                Title = "SharePoint sites with custom permission inheritance",
                Description = $"{uniquePermSites.Count} SharePoint site(s) have unique permissions that do not inherit from the parent. Identity mapping must be complete before migration.",
                AffectedObjectCount = uniquePermSites.Count,
                RemediationSteps =
                [
                    "Complete the identity mapping step before initiating SharePoint migration.",
                    "Review unique permission assignments post-migration and validate access.",
                ],
            });
        }

        // INFO: users without Exchange license
        var unlicensedUsers = users.Where(u => !u.HasMailbox && u.AccountEnabled).ToList();
        if (unlicensedUsers.Count > 0)
        {
            issues.Add(new ScanIssue
            {
                ScanId = scanId,
                Severity = IssueSeverity.Info,
                Category = "license",
                Code = "UNLICENSED_USERS",
                Title = "Users without Exchange Online license",
                Description = $"{unlicensedUsers.Count} user account(s) do not have an Exchange Online license and will not have mailboxes migrated.",
                AffectedObjectCount = unlicensedUsers.Count,
                RemediationSteps =
                [
                    "Confirm these accounts do not require mailbox migration.",
                    "Assign Exchange Online licenses in the target tenant if mailboxes are needed.",
                ],
            });
        }

        return issues;
    }
}
