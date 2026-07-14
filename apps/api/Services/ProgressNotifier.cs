using Microsoft.AspNetCore.SignalR;
using MigrationPlatform.Api.Hubs;

namespace MigrationPlatform.Api.Services;

/// <summary>
/// Publishes real-time progress events to SignalR groups so connected browser
/// clients update without polling.
///
/// Group naming convention:
/// <list type="bullet">
///   <item><c>scan:{scanId}</c> — granular scanner progress</item>
///   <item><c>project:{projectId}</c> — all job/migration progress scoped to a project</item>
/// </list>
///
/// All methods are fire-and-forget safe when called from background workers: a
/// failed send (e.g. no connected clients) must never propagate as an exception.
/// Callers in background workers should wrap calls in try/catch and log at Debug
/// level so a missing SignalR connection does not crash a worker tick.
/// </summary>
public interface IProgressNotifier
{
    /// <summary>
    /// Broadcast discovery-scan progress. Sent to both the scan group and the
    /// project group so both the Scans tab and any scan-detail page update together.
    /// </summary>
    Task NotifyScanProgressAsync(
        Guid scanId,
        Guid projectId,
        int progress,
        string status,
        CancellationToken ct = default);

    /// <summary>Broadcast a generic <see cref="Models.Job"/> status or progress change.</summary>
    Task NotifyJobProgressAsync(
        Guid jobId,
        Guid projectId,
        int progress,
        string status,
        string jobType,
        CancellationToken ct = default);

    /// <summary>Broadcast mailbox migration batch progress.</summary>
    Task NotifyMailboxBatchProgressAsync(
        Guid batchId,
        Guid projectId,
        int synced,
        int total,
        int failed,
        string status,
        CancellationToken ct = default);

    /// <summary>Broadcast OneDrive / SharePoint content migration job progress.</summary>
    Task NotifyContentJobProgressAsync(
        Guid jobId,
        Guid projectId,
        int migrated,
        int total,
        int failed,
        string status,
        CancellationToken ct = default);

    /// <summary>Broadcast post-migration validation run progress.</summary>
    Task NotifyValidationProgressAsync(
        Guid runId,
        Guid projectId,
        int passed,
        int failed,
        int warnings,
        int total,
        string status,
        CancellationToken ct = default);

    /// <summary>Broadcast user migration batch provisioning progress.</summary>
    Task NotifyUserMigrationProgressAsync(
        Guid batchId,
        Guid projectId,
        int provisioned,
        int total,
        int failed,
        string status,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ProgressNotifier : IProgressNotifier
{
    private readonly IHubContext<MigrationHub> _hub;
    private readonly ILogger<ProgressNotifier> _logger;

    public ProgressNotifier(IHubContext<MigrationHub> hub, ILogger<ProgressNotifier> logger)
    {
        _hub    = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyScanProgressAsync(
        Guid scanId,
        Guid projectId,
        int progress,
        string status,
        CancellationToken ct = default)
    {
        var payload = new
        {
            scanId    = scanId.ToString(),
            projectId = projectId.ToString(),
            progress,
            status,
        };

        _logger.LogDebug(
            "ProgressNotifier: ScanProgress scan={ScanId} project={ProjectId} {Progress}% {Status}",
            scanId, projectId, progress, status);

        // Send to the scan-specific group AND the project group so both pages update.
        await _hub.Clients.Group($"scan:{scanId}").SendAsync("ScanProgress", payload, ct);
        await _hub.Clients.Group($"project:{projectId}").SendAsync("ScanProgress", payload, ct);
    }

    /// <inheritdoc />
    public async Task NotifyJobProgressAsync(
        Guid jobId,
        Guid projectId,
        int progress,
        string status,
        string jobType,
        CancellationToken ct = default)
    {
        var payload = new
        {
            jobId     = jobId.ToString(),
            projectId = projectId.ToString(),
            progress,
            status,
            jobType,
        };

        _logger.LogDebug(
            "ProgressNotifier: JobProgress job={JobId} project={ProjectId} {Progress}% {Status}",
            jobId, projectId, progress, status);

        await _hub.Clients.Group($"project:{projectId}").SendAsync("JobProgress", payload, ct);
    }

    /// <inheritdoc />
    public async Task NotifyMailboxBatchProgressAsync(
        Guid batchId,
        Guid projectId,
        int synced,
        int total,
        int failed,
        string status,
        CancellationToken ct = default)
    {
        var progressPercent = total > 0
            ? (int)Math.Round((synced + failed) * 100.0 / total)
            : 0;

        var payload = new
        {
            batchId         = batchId.ToString(),
            projectId       = projectId.ToString(),
            synced,
            total,
            failed,
            status,
            progressPercent,
        };

        _logger.LogDebug(
            "ProgressNotifier: MailboxBatchProgress batch={BatchId} project={ProjectId} synced={Synced}/{Total} failed={Failed} {Status}",
            batchId, projectId, synced, total, failed, status);

        await _hub.Clients.Group($"project:{projectId}").SendAsync("MailboxBatchProgress", payload, ct);
    }

    /// <inheritdoc />
    public async Task NotifyContentJobProgressAsync(
        Guid jobId,
        Guid projectId,
        int migrated,
        int total,
        int failed,
        string status,
        CancellationToken ct = default)
    {
        var progressPercent = total > 0
            ? (int)Math.Round((migrated + failed) * 100.0 / total)
            : 0;

        var payload = new
        {
            jobId           = jobId.ToString(),
            projectId       = projectId.ToString(),
            migrated,
            total,
            failed,
            status,
            progressPercent,
        };

        _logger.LogDebug(
            "ProgressNotifier: ContentJobProgress job={JobId} project={ProjectId} migrated={Migrated}/{Total} failed={Failed} {Status}",
            jobId, projectId, migrated, total, failed, status);

        await _hub.Clients.Group($"project:{projectId}").SendAsync("ContentJobProgress", payload, ct);
    }

    /// <inheritdoc />
    public async Task NotifyValidationProgressAsync(
        Guid runId,
        Guid projectId,
        int passed,
        int failed,
        int warnings,
        int total,
        string status,
        CancellationToken ct = default)
    {
        var checked_ = passed + failed + warnings;
        var progressPercent = total > 0
            ? (int)Math.Round(checked_ * 100.0 / total)
            : 0;

        var payload = new
        {
            runId           = runId.ToString(),
            projectId       = projectId.ToString(),
            passed,
            failed,
            warnings,
            total,
            status,
            progressPercent,
        };

        _logger.LogDebug(
            "ProgressNotifier: ValidationProgress run={RunId} project={ProjectId} passed={Passed} failed={Failed} warnings={Warnings}/{Total} {Status}",
            runId, projectId, passed, failed, warnings, total, status);

        await _hub.Clients.Group($"project:{projectId}").SendAsync("ValidationProgress", payload, ct);
    }

    /// <inheritdoc />
    public async Task NotifyUserMigrationProgressAsync(
        Guid batchId,
        Guid projectId,
        int provisioned,
        int total,
        int failed,
        string status,
        CancellationToken ct = default)
    {
        var progressPercent = total > 0
            ? (int)Math.Round((provisioned + failed) * 100.0 / total)
            : 0;

        var payload = new
        {
            batchId         = batchId.ToString(),
            projectId       = projectId.ToString(),
            provisioned,
            total,
            failed,
            status,
            progressPercent,
        };

        _logger.LogDebug(
            "ProgressNotifier: UserMigrationProgress batch={BatchId} project={ProjectId} provisioned={Provisioned}/{Total} failed={Failed} {Status}",
            batchId, projectId, provisioned, total, failed, status);

        await _hub.Clients.Group($"project:{projectId}").SendAsync("UserMigrationProgress", payload, ct);
    }
}
