using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Workers;
using NSubstitute;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Behavioural coverage of <c>MailboxMigrationWorker.UpdateEntriesFromExoUsersAsync</c>
/// — the per-user EXO status projection. The method is a private instance method
/// with no public/internal seam, so it is invoked via reflection against a real
/// worker instance with a substituted repository; it mutates the entry objects
/// in place, which the assertions inspect. (Two behaviours here were live
/// findings: NeedsApproval → Failed-with-license-message, and tolerance of
/// duplicate EXO rows for one address.)
///
/// NOTE for maintainers: if this reflection call ever breaks, promote
/// UpdateEntriesFromExoUsersAsync to <c>internal</c> and call it directly.
/// </summary>
public class MailboxWorkerBehaviorTests
{
    private static readonly MethodInfo UpdateEntries =
        typeof(MailboxMigrationWorker).GetMethod(
            "UpdateEntriesFromExoUsersAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "UpdateEntriesFromExoUsersAsync not found — signature changed? Update MailboxWorkerBehaviorTests.");

    private static MailboxMigrationWorker NewWorker()
    {
        var config = new ConfigurationBuilder().Build();
        return new MailboxMigrationWorker(
            new MailboxMigrationQueue(),
            Substitute.For<IServiceProvider>(),
            config,
            NullLogger<MailboxMigrationWorker>.Instance);
    }

    private static async Task InvokeUpdateAsync(
        MailboxMigrationBatch batch,
        IReadOnlyList<MailboxMigrationEntry> entries,
        IReadOnlyList<ExoMigrationUser> exoUsers)
    {
        var repo = Substitute.For<IMailboxMigrationRepository>();
        repo.GetEntriesByBatchAsync(batch.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<MailboxMigrationEntry>>(entries));

        var task = (Task)UpdateEntries.Invoke(
            NewWorker(),
            new object[] { repo, batch, exoUsers, CancellationToken.None })!;
        await task;
    }

    private static MailboxMigrationEntry Entry(string sourceUpn) =>
        new() { SourceUpn = sourceUpn, TargetUpn = sourceUpn.Replace("source", "target"), Status = MailboxMigrationStatus.Syncing };

    // ── Area 2: NeedsApproval → Failed with the license error ────────────────

    [Fact]
    public async Task NeedsApproval_marks_entry_Failed_with_license_guidance()
    {
        var batch = new MailboxMigrationBatch { Name = "b", Status = BatchStatus.Syncing };
        var entry = Entry("bill@source.com");
        await InvokeUpdateAsync(batch, new[] { entry },
            new[] { new ExoMigrationUser("bill@source.com", "NeedsApproval", null) });

        Assert.Equal(MailboxMigrationStatus.Failed, entry.Status);
        Assert.NotNull(entry.ErrorMessage);
        Assert.Contains("Cross Tenant User Data Migration", entry.ErrorMessage);
        Assert.Contains("license", entry.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NeedsApproval_is_case_insensitive_and_appends_exo_detail()
    {
        var batch = new MailboxMigrationBatch { Name = "b", Status = BatchStatus.Syncing };
        var entry = Entry("bill@source.com");
        await InvokeUpdateAsync(batch, new[] { entry },
            new[] { new ExoMigrationUser("bill@source.com", "  needsApproval ", "MRS said no") });

        Assert.Equal(MailboxMigrationStatus.Failed, entry.Status);
        Assert.Contains("Cross Tenant User Data Migration", entry.ErrorMessage!);
        Assert.Contains("MRS said no", entry.ErrorMessage!);
    }

    // ── Area 1: duplicate EmailAddress rows are tolerated (no throw) ──────────

    [Fact]
    public async Task Duplicate_exo_rows_for_one_address_do_not_throw_and_last_wins()
    {
        var batch = new MailboxMigrationBatch { Name = "b", Status = BatchStatus.Syncing };
        var entry = Entry("dupe@source.com");

        // Two rows for the same address — the old ToDictionary would have thrown.
        // Last row ("Failed") must win over the first ("Syncing").
        var ex = await Record.ExceptionAsync(() => InvokeUpdateAsync(batch, new[] { entry },
            new[]
            {
                new ExoMigrationUser("dupe@source.com", "Syncing", null),
                new ExoMigrationUser("dupe@source.com", "Failed", "boom"),
            }));

        Assert.Null(ex);
        Assert.Equal(MailboxMigrationStatus.Failed, entry.Status);
        Assert.Equal("boom", entry.ErrorMessage);
    }

    [Fact]
    public async Task Rows_with_blank_address_are_skipped_without_error()
    {
        var batch = new MailboxMigrationBatch { Name = "b", Status = BatchStatus.Syncing };
        var entry = Entry("real@source.com");
        var ex = await Record.ExceptionAsync(() => InvokeUpdateAsync(batch, new[] { entry },
            new[]
            {
                new ExoMigrationUser("", "Failed", "ignore me"),
                new ExoMigrationUser("real@source.com", "Synced", null),
            }));

        Assert.Null(ex);
        Assert.Equal(MailboxMigrationStatus.Synced, entry.Status);
        Assert.Equal(100, entry.ItemsSyncedPercent);
    }

    // ── Ordinary projection: unmatched entries and unknown statuses ──────────

    [Fact]
    public async Task Entry_with_no_matching_exo_row_is_left_untouched()
    {
        var batch = new MailboxMigrationBatch { Name = "b", Status = BatchStatus.Syncing };
        var entry = Entry("nomatch@source.com");
        entry.Status = MailboxMigrationStatus.Queued;

        await InvokeUpdateAsync(batch, new[] { entry },
            new[] { new ExoMigrationUser("someoneelse@source.com", "Synced", null) });

        Assert.Equal(MailboxMigrationStatus.Queued, entry.Status);
    }

    [Fact]
    public async Task Synced_status_sets_percent_to_100()
    {
        var batch = new MailboxMigrationBatch { Name = "b", Status = BatchStatus.Syncing };
        var entry = Entry("done@source.com");
        await InvokeUpdateAsync(batch, new[] { entry },
            new[] { new ExoMigrationUser("done@source.com", "Synced", null) });

        Assert.Equal(MailboxMigrationStatus.Synced, entry.Status);
        Assert.Equal(100, entry.ItemsSyncedPercent);
    }
}
