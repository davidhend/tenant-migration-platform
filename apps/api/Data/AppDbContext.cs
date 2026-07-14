using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data;

/// <summary>
/// EF Core DbContext for the Migration Platform.  All entity configurations
/// are applied automatically from <see cref="Configuration"/> classes via
/// <see cref="ModelBuilder.ApplyConfigurationsFromAssembly"/>.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── Core entities ────────────────────────────────────────────────────────
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<MigrationProject> Projects => Set<MigrationProject>();

    // ── Scan and sub-collections ─────────────────────────────────────────────
    public DbSet<Scan> Scans => Set<Scan>();
    public DbSet<ScannedUser> ScannedUsers => Set<ScannedUser>();
    public DbSet<ScannedGroup> ScannedGroups => Set<ScannedGroup>();
    public DbSet<ScannedMailbox> ScannedMailboxes => Set<ScannedMailbox>();
    public DbSet<ScannedSite> ScannedSites => Set<ScannedSite>();
    public DbSet<ScannedOneDrive> ScannedOneDrives => Set<ScannedOneDrive>();
    public DbSet<ScannedDomain> ScannedDomains => Set<ScannedDomain>();
    public DbSet<ScanIssue> ScanIssues => Set<ScanIssue>();

    // ── Identity, jobs, audit ─────────────────────────────────────────────────
    public DbSet<IdentityMap> IdentityMaps => Set<IdentityMap>();
    public DbSet<DomainRule> DomainRules => Set<DomainRule>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<LocalCredential> LocalCredentials => Set<LocalCredential>();

    // ── Mailbox migration ─────────────────────────────────────────────────────
    public DbSet<MailboxMigrationBatch> MailboxMigrationBatches => Set<MailboxMigrationBatch>();
    public DbSet<MailboxMigrationEntry> MailboxMigrationEntries => Set<MailboxMigrationEntry>();

    // ── User migration (Graph POST /users) ────────────────────────────────────
    public DbSet<UserMigrationBatch> UserMigrationBatches => Set<UserMigrationBatch>();
    public DbSet<UserMigrationEntry> UserMigrationEntries => Set<UserMigrationEntry>();

    // ── Content migration (OneDrive / SharePoint) ─────────────────────────────
    public DbSet<ContentMigrationJob> ContentMigrationJobs => Set<ContentMigrationJob>();
    public DbSet<ContentMigrationItem> ContentMigrationItems => Set<ContentMigrationItem>();

    // ── Domain cutover ────────────────────────────────────────────────────────
    public DbSet<DomainCutoverJob> DomainCutoverJobs => Set<DomainCutoverJob>();

    // ── Wave planner ──────────────────────────────────────────────────────────
    public DbSet<MigrationWave> MigrationWaves => Set<MigrationWave>();

    // ── Post-migration validation ─────────────────────────────────────────────
    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();
    public DbSet<ValidationCheck> ValidationChecks => Set<ValidationCheck>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Discovers and applies all IEntityTypeConfiguration<T> classes in this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
