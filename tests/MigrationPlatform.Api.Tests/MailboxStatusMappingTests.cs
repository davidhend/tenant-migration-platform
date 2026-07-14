using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Workers;

namespace MigrationPlatform.Api.Tests;

public class MapExoUserStatusTests
{
    [Theory]
    [InlineData("completed", MailboxMigrationStatus.Synced)]
    [InlineData("synced", MailboxMigrationStatus.Synced)]
    [InlineData("completedwithwarnings", MailboxMigrationStatus.Synced)]
    [InlineData("failed", MailboxMigrationStatus.Failed)]
    [InlineData("corruptdata", MailboxMigrationStatus.Failed)]
    [InlineData("completionfailed", MailboxMigrationStatus.Failed)]
    [InlineData("queued", MailboxMigrationStatus.Queued)]
    [InlineData("provisioning", MailboxMigrationStatus.Queued)]
    [InlineData("provisioned", MailboxMigrationStatus.Queued)]
    [InlineData("stopped", MailboxMigrationStatus.Queued)]
    [InlineData("syncing", MailboxMigrationStatus.Syncing)]
    [InlineData("synced_partial", MailboxMigrationStatus.Syncing)]
    [InlineData("incrementalsyncing", MailboxMigrationStatus.Syncing)]
    [InlineData("completing", MailboxMigrationStatus.Syncing)]
    [InlineData("completionsynced", MailboxMigrationStatus.Syncing)]
    [InlineData("completioninprogress", MailboxMigrationStatus.Syncing)]
    public void Maps_known_exo_statuses(string exoStatus, MailboxMigrationStatus expected)
        => Assert.Equal(expected, MailboxMigrationWorker.MapExoUserStatus(exoStatus));

    [Theory]
    [InlineData("Completed", MailboxMigrationStatus.Synced)]
    [InlineData("  COMPLETING  ", MailboxMigrationStatus.Syncing)]
    [InlineData("\tFailed\n", MailboxMigrationStatus.Failed)]
    public void Is_case_insensitive_and_trims(string exoStatus, MailboxMigrationStatus expected)
        => Assert.Equal(expected, MailboxMigrationWorker.MapExoUserStatus(exoStatus));

    [Theory]
    [InlineData("somefuturestatus")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Unknown_or_empty_maps_to_null(string? exoStatus)
        => Assert.Null(MailboxMigrationWorker.MapExoUserStatus(exoStatus));

    // NeedsApproval (missing Cross Tenant User Data Migration license) is
    // deliberately NOT mapped here — the worker special-cases it BEFORE calling
    // MapExoUserStatus and fails the entry with an actionable license message.
    // TODO: cover that branch with an integration test over the poll loop.
    [Fact]
    public void NeedsApproval_is_not_silently_mapped()
        => Assert.Null(MailboxMigrationWorker.MapExoUserStatus("needsapproval"));
}

public class IsExoTerminalStatusTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("Completed")]
    [InlineData("completedwithwarnings")]
    [InlineData("failed")]
    [InlineData("stopped")]
    [InlineData("removed")]
    public void Terminal_statuses_are_terminal(string status)
        => Assert.True(MailboxMigrationWorker.IsExoTerminalStatus(status));

    [Theory]
    [InlineData("syncing")]
    [InlineData("synced")]                // parked awaiting cutover — NOT terminal
    [InlineData("completing")]
    [InlineData("completionsynced")]
    [InlineData("incrementalsyncing")]
    [InlineData("needsapproval")]
    [InlineData("queued")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_terminal_statuses_are_not_terminal(string? status)
        => Assert.False(MailboxMigrationWorker.IsExoTerminalStatus(status));
}
