using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Extensions;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Discovery.Scanners;
using MigrationPlatform.Api.Services.Discovery.Analyzers;
using MigrationPlatform.Api.Services;
using System.Collections.Generic;

namespace MigrationPlatform.Api.Services.Discovery;

/// <summary>
/// Orchestrates the full tenant discovery pipeline: runs each scanner in sequence,
/// persists results to the database via <see cref="IScanRepository"/>, and computes
/// the readiness summary and issue list.
///
/// After each progress checkpoint <see cref="IProgressNotifier"/> pushes a
/// <c>ScanProgress</c> SignalR event to both the scan group and the project group,
/// so the UI updates in real time without polling.
/// </summary>
public class DiscoveryEngine : IDiscoveryEngine
{
    private readonly IScanRepository _scanRepo;
    private readonly UserScanner _userScanner;
    private readonly GroupScanner _groupScanner;
    private readonly MailboxScanner _mailboxScanner;
    private readonly SharePointScanner _siteScanner;
    private readonly OneDriveScanner _oneDriveScanner;
    private readonly DomainScanner _domainScanner;
    private readonly ReadinessAnalyzer _readinessAnalyzer;
    private readonly IssueDetector _issueDetector;
    private readonly IDomainRuleRepository _domainRules;
    private readonly IProgressNotifier _notifier;
    private readonly ILogger<DiscoveryEngine> _logger;

    public DiscoveryEngine(
        IScanRepository scanRepo,
        UserScanner userScanner,
        GroupScanner groupScanner,
        MailboxScanner mailboxScanner,
        SharePointScanner siteScanner,
        OneDriveScanner oneDriveScanner,
        DomainScanner domainScanner,
        ReadinessAnalyzer readinessAnalyzer,
        IssueDetector issueDetector,
        IDomainRuleRepository domainRules,
        IProgressNotifier notifier,
        ILogger<DiscoveryEngine> logger)
    {
        _scanRepo = scanRepo;
        _userScanner = userScanner;
        _groupScanner = groupScanner;
        _mailboxScanner = mailboxScanner;
        _siteScanner = siteScanner;
        _oneDriveScanner = oneDriveScanner;
        _domainScanner = domainScanner;
        _readinessAnalyzer = readinessAnalyzer;
        _issueDetector = issueDetector;
        _domainRules = domainRules;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<Scan> RunScanAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        var scan = await _scanRepo.GetByIdAsync(scanId, cancellationToken)
            ?? throw new KeyNotFoundException($"Scan {scanId} not found.");

        _logger.LogInformation("Starting scan {ScanId} for tenant {TenantId}", scanId, scan.TenantId);

        try
        {
            scan.Status = ScanStatus.Running;
            scan.StartedAt = DateTime.UtcNow;
            scan.Progress = 0;
            await _scanRepo.SaveAsync(cancellationToken);

            await SetProgress(scan, 5, cancellationToken);

            var users = await _userScanner.ScanAsync(scan.TenantId, scanId, cancellationToken);
            await _scanRepo.ReplaceUsersAsync(scanId, users, cancellationToken);
            await SetProgress(scan, 25, cancellationToken);

            var groups = await _groupScanner.ScanAsync(scan.TenantId, scanId, cancellationToken);
            await _scanRepo.ReplaceGroupsAsync(scanId, groups, cancellationToken);
            await SetProgress(scan, 40, cancellationToken);

            var mailboxes = await _mailboxScanner.ScanAsync(scan.TenantId, scanId, users, cancellationToken);
            await _scanRepo.ReplaceMailboxesAsync(scanId, mailboxes, cancellationToken);
            await SetProgress(scan, 55, cancellationToken);

            var sites = await _siteScanner.ScanAsync(scan.TenantId, scanId, cancellationToken);
            await _scanRepo.ReplaceSitesAsync(scanId, sites, cancellationToken);
            await SetProgress(scan, 70, cancellationToken);

            var onedrive = await _oneDriveScanner.ScanAsync(scan.TenantId, scanId, users, cancellationToken);
            await _scanRepo.ReplaceOneDriveAsync(scanId, onedrive, cancellationToken);
            await SetProgress(scan, 82, cancellationToken);

            var domains = await _domainScanner.ScanAsync(scan.TenantId, scanId, users, cancellationToken);
            await _scanRepo.ReplaceDomainsAsync(scanId, domains, cancellationToken);
            await SetProgress(scan, 90, cancellationToken);

            // Load domain rules for this project so the issue detector can suppress
            // blockers that are already addressed by a transformation rule.
            IEnumerable<DomainRule> projectRules = [];
            if (scan.ProjectId.HasValue)
                projectRules = await _domainRules.GetByProjectAsync(scan.ProjectId.Value, cancellationToken);

            // Analyze results
            var issues = _issueDetector.Detect(scanId, users, mailboxes, sites, domains, projectRules);
            await _scanRepo.ReplaceIssuesAsync(scanId, issues, cancellationToken);

            var score = _readinessAnalyzer.ComputeScore(users, mailboxes, sites, domains, issues);

            scan.Summary = new ScanSummary
            {
                UserCount = users.Count,
                GroupCount = groups.Count,
                MailboxCount = mailboxes.Count,
                MailboxTotalSizeGb = Math.Round(mailboxes.Sum(m => m.SizeGb), 1),
                SiteCount = sites.Count,
                OneDriveCount = onedrive.Count,
                DomainCount = domains.Count,
                BlockerCount = issues.Count(i => i.Severity == IssueSeverity.Blocker),
                WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning),
                ReadinessScore = score,
            };

            scan.Status = ScanStatus.Completed;
            scan.Progress = 100;
            scan.CompletedAt = DateTime.UtcNow;
            await _scanRepo.SaveAsync(cancellationToken);

            // Final notification — progress = 100, status = Completed.
            if (scan.ProjectId.HasValue)
            {
                try
                {
                    await _notifier.NotifyScanProgressAsync(
                        scan.Id, scan.ProjectId.Value, 100, ScanStatus.Completed.ToCamelCase(), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "DiscoveryEngine: SignalR completion notification failed for scan {ScanId}.", scan.Id);
                }
            }

            _logger.LogInformation(
                "Scan {ScanId} completed. Score: {Score}, Users: {Users}",
                scanId, score, users.Count);
        }
        catch (OperationCanceledException)
        {
            scan.Status = ScanStatus.Failed;
            scan.ErrorMessage = "Scan was cancelled.";
            await _scanRepo.SaveAsync(CancellationToken.None);
            _logger.LogWarning("Scan {ScanId} was cancelled.", scanId);

            if (scan.ProjectId.HasValue)
            {
                try
                {
                    await _notifier.NotifyScanProgressAsync(
                        scan.Id, scan.ProjectId.Value, scan.Progress, ScanStatus.Failed.ToCamelCase(), CancellationToken.None);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogDebug(notifyEx, "DiscoveryEngine: SignalR cancellation notification failed for scan {ScanId}.", scan.Id);
                }
            }
        }
        catch (Exception ex)
        {
            scan.Status = ScanStatus.Failed;
            scan.ErrorMessage = ex.Message;
            await _scanRepo.SaveAsync(CancellationToken.None);
            _logger.LogError(ex, "Scan {ScanId} failed.", scanId);

            if (scan.ProjectId.HasValue)
            {
                try
                {
                    await _notifier.NotifyScanProgressAsync(
                        scan.Id, scan.ProjectId.Value, scan.Progress, ScanStatus.Failed.ToCamelCase(), CancellationToken.None);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogDebug(notifyEx, "DiscoveryEngine: SignalR failure notification failed for scan {ScanId}.", scan.Id);
                }
            }
        }

        return scan;
    }

    private async Task SetProgress(Scan scan, int progress, CancellationToken ct)
    {
        scan.Progress = progress;
        await _scanRepo.SaveAsync(ct);

        // Push real-time progress to any connected clients.  A failed notification
        // (e.g. no connected clients) must never crash the scan pipeline.
        if (scan.ProjectId.HasValue)
        {
            try
            {
                await _notifier.NotifyScanProgressAsync(
                    scan.Id,
                    scan.ProjectId.Value,
                    progress,
                    scan.Status.ToCamelCase(),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DiscoveryEngine: SignalR notification failed for scan {ScanId} — continuing.", scan.Id);
            }
        }

    }
}
