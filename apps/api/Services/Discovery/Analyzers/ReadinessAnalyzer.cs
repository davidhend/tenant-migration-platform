using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Services.Discovery.Analyzers;

/// <summary>
/// Computes a 0–100 readiness score from scan results.
/// Start at 100 and deduct points for identified issues.
/// </summary>
public class ReadinessAnalyzer
{
    public int ComputeScore(
        List<ScannedUser> users,
        List<ScannedMailbox> mailboxes,
        List<ScannedSite> sites,
        List<ScannedDomain> domains,
        List<ScanIssue> issues)
    {
        int score = 100;

        // Deduct for blockers
        var blockers = issues.Count(i => i.Severity == IssueSeverity.Blocker);
        score -= blockers * 15;

        // Deduct for warnings
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        score -= warnings * 3;

        // Deduct for large mailboxes (>50GB)
        var largeMailboxes = mailboxes.Count(m => m.SizeGb > 50);
        score -= largeMailboxes * 2;

        // Deduct for disabled users with mailboxes (stale accounts)
        var disabledWithMailbox = users.Count(u => !u.AccountEnabled && u.HasMailbox);
        if (disabledWithMailbox > 10) score -= 3;

        // Deduct if any domains not verified
        var unverifiedDomains = domains.Count(d => !d.IsVerified);
        score -= unverifiedDomains * 5;

        // Deduct for sites with unique permissions needing attention
        var uniquePermSites = sites.Count(s => s.HasUniquePermissions);
        if (uniquePermSites > 0) score -= Math.Min(uniquePermSites, 3);

        return Math.Max(0, Math.Min(100, score));
    }
}
