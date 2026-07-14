using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Workers;
using Xunit;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Covers the batch-level EXO status mapping (MailboxMigrationWorker.NextBatchStatusFromExo),
/// including the regression this session's fix addressed: SyncedWithErrors with nothing
/// synced must be Failed, not parked at Synced (which masked the failure and blocked retry).
/// </summary>
public class BatchStatusMappingTests
{
    [Theory]
    // SyncedWithErrors + nothing synced + some failed = FAILURE (the masking regression)
    [InlineData("SyncedWithErrors", 0, 1, BatchStatus.Syncing, BatchStatus.Failed)]
    [InlineData("SyncedWithErrors", 0, 3, BatchStatus.Synced, BatchStatus.Failed)]
    // SyncedWithErrors but SOMETHING synced = park at Synced (awaiting cutover), not Failed
    [InlineData("SyncedWithErrors", 2, 1, BatchStatus.Syncing, BatchStatus.Synced)]
    // Clean synced from Syncing = awaiting cutover
    [InlineData("Synced", 1, 0, BatchStatus.Syncing, BatchStatus.Synced)]
    [InlineData("IncrementalSyncing", 1, 0, BatchStatus.Syncing, BatchStatus.Synced)]
    // Completion statuses from Syncing/Synced
    [InlineData("Completing", 1, 0, BatchStatus.Syncing, BatchStatus.Completing)]
    [InlineData("CompletionSynced", 1, 0, BatchStatus.Synced, BatchStatus.Completing)]
    [InlineData("CompletionInProgress", 1, 0, BatchStatus.Synced, BatchStatus.Completing)]
    // Case-insensitive + trimming
    [InlineData("  syncedwitherrors  ", 0, 1, BatchStatus.Syncing, BatchStatus.Failed)]
    public void Maps_expected_transition(string exo, int synced, int failed, BatchStatus current, BatchStatus expected)
        => Assert.Equal(expected, MailboxMigrationWorker.NextBatchStatusFromExo(exo, synced, failed, current));

    [Theory]
    [InlineData("Syncing", 0, 0, BatchStatus.Syncing)]      // still syncing → no transition
    [InlineData("Synced", 1, 0, BatchStatus.Synced)]        // already parked → no re-transition
    [InlineData("", 0, 0, BatchStatus.Syncing)]             // empty status
    [InlineData(null, 0, 0, BatchStatus.Syncing)]           // null status
    [InlineData("Completing", 1, 0, BatchStatus.Completed)] // terminal current → no transition
    public void Returns_null_when_no_transition(string? exo, int synced, int failed, BatchStatus current)
        => Assert.Null(MailboxMigrationWorker.NextBatchStatusFromExo(exo, synced, failed, current));
}
