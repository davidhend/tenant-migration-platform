using MigrationPlatform.Api.Controllers;
using MigrationPlatform.Api.Models;
using Xunit;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Covers the mailbox-first ordering gate (UserMigrationController.FindMailboxOverlaps):
/// users whose mailbox migrates are provisioned by the mailbox flow's New-MailUser, so
/// they must be blocked from user migration — a pre-created member account at the same
/// UPN breaks the MailUser provisioning, and after the move the account already exists.
/// </summary>
public class UserMigrationOrderingTests
{
    private static UserMigrationEntry User(
        string source, string target, UserMigrationEntryStatus status = UserMigrationEntryStatus.Queued) =>
        new() { SourceUpn = source, TargetUpn = target, Status = status };

    private static MailboxMigrationEntry Mailbox(
        string source, string target, MailboxMigrationStatus status = MailboxMigrationStatus.Queued) =>
        new() { SourceUpn = source, TargetUpn = target, Status = status };

    [Fact]
    public void No_overlap_returns_empty()
    {
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [User("a@src.com", "a@tgt.com")],
            [Mailbox("b@src.com", "b@tgt.com")]);
        Assert.Empty(overlaps);
    }

    [Fact]
    public void Source_upn_match_is_blocked()
    {
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [User("a@src.com", "a@tgt.com")],
            [Mailbox("a@src.com", "different@tgt.com")]);
        Assert.Equal(["a@src.com"], overlaps);
    }

    [Fact]
    public void Target_upn_match_is_blocked()
    {
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [User("different@src.com", "a@tgt.com")],
            [Mailbox("a@src.com", "a@tgt.com")]);
        Assert.Equal(["different@src.com"], overlaps);
    }

    [Fact]
    public void Match_is_case_insensitive_and_trimmed()
    {
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [User("A@SRC.com", "a@tgt.com")],
            [Mailbox(" a@src.COM ", "x@tgt.com")]);
        Assert.Single(overlaps);
    }

    [Fact]
    public void Skipped_mailbox_entries_do_not_block()
    {
        // A skipped mailbox entry means the mailbox is explicitly not migrating —
        // user migration is then the legitimate way to bring the account over.
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [User("a@src.com", "a@tgt.com")],
            [Mailbox("a@src.com", "a@tgt.com", MailboxMigrationStatus.Skipped)]);
        Assert.Empty(overlaps);
    }

    [Theory]
    [InlineData(MailboxMigrationStatus.Queued)]
    [InlineData(MailboxMigrationStatus.Syncing)]
    [InlineData(MailboxMigrationStatus.Synced)]
    [InlineData(MailboxMigrationStatus.Failed)]
    public void Every_non_skipped_mailbox_status_blocks(MailboxMigrationStatus status)
    {
        // Pending → mailbox must run first; done → the account already exists via
        // the mailbox flow; failed → a retry of the mailbox batch must stay possible.
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [User("a@src.com", "a@tgt.com")],
            [Mailbox("a@src.com", "a@tgt.com", status)]);
        Assert.Single(overlaps);
    }

    [Fact]
    public void Provisioned_and_skipped_user_entries_are_ignored()
    {
        // Already-provisioned or skipped user entries are not re-run by start/retry,
        // so they must not block the rest of the batch.
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [
                User("a@src.com", "a@tgt.com", UserMigrationEntryStatus.Provisioned),
                User("b@src.com", "b@tgt.com", UserMigrationEntryStatus.Skipped),
                User("c@src.com", "c@tgt.com"),
            ],
            [
                Mailbox("a@src.com", "a@tgt.com"),
                Mailbox("b@src.com", "b@tgt.com"),
            ]);
        Assert.Empty(overlaps);
    }

    [Fact]
    public void Overlaps_are_deduplicated_and_sorted()
    {
        var overlaps = UserMigrationController.FindMailboxOverlaps(
            [
                User("b@src.com", "b@tgt.com"),
                User("a@src.com", "a@tgt.com"),
            ],
            [
                Mailbox("a@src.com", "a@tgt.com"),
                Mailbox("a@src.com", "a@tgt.com"),
                Mailbox("b@src.com", "b@tgt.com"),
            ]);
        Assert.Equal(["a@src.com", "b@src.com"], overlaps);
    }
}
