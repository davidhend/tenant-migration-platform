using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Repository integration tests against in-memory SQLite through the real
/// AppDbContext + EF model — exercising the "active work" queries the workers
/// depend on for rehydration, and round-tripping the content/domain-cutover
/// entities end to end.
/// </summary>
public class RepositoryIntegrationTests : IDisposable
{
    private readonly SqliteHarness _h = new();
    public void Dispose() => _h.Dispose();

    // ── Mailbox: batch + entries round-trip and active-batch filtering ───────

    [Fact]
    public async Task Mailbox_batch_and_entries_round_trip_and_active_filter_matches_worker_contract()
    {
        var (projectId, _, _) = _h.SeedProject();

        var syncing  = new MailboxMigrationBatch { ProjectId = projectId, Name = "syncing",  Status = BatchStatus.Syncing };
        var synced   = new MailboxMigrationBatch { ProjectId = projectId, Name = "synced",   Status = BatchStatus.Synced };
        var completing = new MailboxMigrationBatch { ProjectId = projectId, Name = "completing", Status = BatchStatus.Completing };
        var draft    = new MailboxMigrationBatch { ProjectId = projectId, Name = "draft",    Status = BatchStatus.Draft };
        var completed = new MailboxMigrationBatch { ProjectId = projectId, Name = "completed", Status = BatchStatus.Completed };

        await using (var ctx = _h.NewContext())
        {
            ctx.AddRange(syncing, synced, completing, draft, completed);
            ctx.MailboxMigrationEntries.AddRange(
                new MailboxMigrationEntry { BatchId = syncing.Id, SourceUpn = "a@s.com", TargetUpn = "a@t.com", Status = MailboxMigrationStatus.Syncing },
                new MailboxMigrationEntry { BatchId = syncing.Id, SourceUpn = "b@s.com", TargetUpn = "b@t.com", Status = MailboxMigrationStatus.Failed, ErrorMessage = "x" });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _h.NewContext())
        {
            var repo = new MailboxMigrationRepository(ctx);

            // Active = Syncing | Synced | Completing (the set the worker rehydrates).
            var active = (await repo.GetActiveBatchesAsync()).Select(b => b.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "completing", "synced", "syncing" }, active);

            var entries = (await repo.GetEntriesByBatchAsync(syncing.Id)).OrderBy(e => e.SourceUpn).ToList();
            Assert.Equal(2, entries.Count);
            Assert.Equal(MailboxMigrationStatus.Failed, entries[1].Status);
            Assert.Equal("x", entries[1].ErrorMessage);
        }
    }

    [Fact]
    public async Task Mailbox_batch_status_transition_persists()
    {
        var (projectId, _, _) = _h.SeedProject();
        var batch = new MailboxMigrationBatch { ProjectId = projectId, Name = "b", Status = BatchStatus.Syncing };
        await using (var ctx = _h.NewContext()) { ctx.Add(batch); await ctx.SaveChangesAsync(); }

        await using (var ctx = _h.NewContext())
        {
            var repo = new MailboxMigrationRepository(ctx);
            var loaded = await repo.GetBatchByIdAsync(batch.Id);
            loaded!.Status = BatchStatus.Failed;
            loaded.ErrorMessage = "MRS initial sync failed";
            await repo.SaveAsync();
        }

        await using (var ctx = _h.NewContext())
        {
            var reloaded = await new MailboxMigrationRepository(ctx).GetBatchByIdAsync(batch.Id);
            Assert.Equal(BatchStatus.Failed, reloaded!.Status);
            Assert.Equal("MRS initial sync failed", reloaded.ErrorMessage);
        }
    }

    // ── Content: job round-trip and active-job filter ────────────────────────

    [Fact]
    public async Task Content_job_round_trips_and_active_filter_is_running_or_provisioning()
    {
        var (projectId, _, _) = _h.SeedProject();

        var running      = new ContentMigrationJob { ProjectId = projectId, Name = "run",  JobType = ContentMigrationJobType.OneDrive,  Status = ContentMigrationJobStatus.Running };
        var provisioning = new ContentMigrationJob { ProjectId = projectId, Name = "prov", JobType = ContentMigrationJobType.OneDrive,  Status = ContentMigrationJobStatus.Provisioning };
        var draft        = new ContentMigrationJob { ProjectId = projectId, Name = "draft", JobType = ContentMigrationJobType.SharePoint, Status = ContentMigrationJobStatus.Draft };
        var completed    = new ContentMigrationJob { ProjectId = projectId, Name = "done", JobType = ContentMigrationJobType.SharePoint, Status = ContentMigrationJobStatus.Completed };

        await using (var ctx = _h.NewContext()) { ctx.AddRange(running, provisioning, draft, completed); await ctx.SaveChangesAsync(); }

        await using (var ctx = _h.NewContext())
        {
            var repo = new ContentMigrationRepository(ctx);
            var active = (await repo.GetActiveJobsAsync()).Select(j => j.Name).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "prov", "run" }, active);

            var loaded = await repo.GetJobByIdAsync(draft.Id);
            Assert.Equal(ContentMigrationJobType.SharePoint, loaded!.JobType);
            Assert.Equal(ContentMigrationJobStatus.Draft, loaded.Status);
        }
    }

    // ── Domain cutover: round-trip and active-phase filter ───────────────────

    [Fact]
    public async Task Domain_cutover_round_trips_and_active_excludes_created_pause_and_terminal_phases()
    {
        var (projectId, _, _) = _h.SeedProject();

        var created     = new DomainCutoverJob { ProjectId = projectId, DomainName = "created.com",   Phase = DomainCutoverPhase.Created };
        var cleaning    = new DomainCutoverJob { ProjectId = projectId, DomainName = "cleaning.com",  Phase = DomainCutoverPhase.CleaningSource };
        var awaitingDns = new DomainCutoverJob { ProjectId = projectId, DomainName = "dns.com",       Phase = DomainCutoverPhase.AwaitingDnsVerification, DnsVerificationRecord = "MS=ms12345" };
        var awaitingMx  = new DomainCutoverJob { ProjectId = projectId, DomainName = "mx.com",        Phase = DomainCutoverPhase.AwaitingMxUpdate, TargetMxRecord = "target.mail.protection.outlook.com" };
        var completed   = new DomainCutoverJob { ProjectId = projectId, DomainName = "done.com",      Phase = DomainCutoverPhase.Completed };

        await using (var ctx = _h.NewContext()) { ctx.AddRange(created, cleaning, awaitingDns, awaitingMx, completed); await ctx.SaveChangesAsync(); }

        await using (var ctx = _h.NewContext())
        {
            var repo = new DomainCutoverRepository(ctx);

            // Active = worker-processable phases only (not created / pause / terminal).
            var active = (await repo.GetActiveJobsAsync()).Select(j => j.DomainName).ToList();
            Assert.Equal(new[] { "cleaning.com" }, active);

            // Pause-phase jobs round-trip with the DNS/MX values the UI must show.
            var dns = await repo.GetByIdAsync(awaitingDns.Id);
            Assert.Equal("MS=ms12345", dns!.DnsVerificationRecord);
            var mx = await repo.GetByIdAsync(awaitingMx.Id);
            Assert.Equal("target.mail.protection.outlook.com", mx!.TargetMxRecord);
        }
    }
}
