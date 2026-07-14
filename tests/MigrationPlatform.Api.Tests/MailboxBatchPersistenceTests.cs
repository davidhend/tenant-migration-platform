using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Guards enum/persistence drift: BatchStatus.Synced was appended to the enum
/// (awaiting-cutover state) — these tests fail if reordering ever changes the
/// persisted value or the camelCase wire contract the frontend depends on.
/// </summary>
public class MailboxBatchPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    /// <summary>
    /// AppDbContext with the single Npgsql-only artifact removed so SQLite can
    /// create the schema: ScannedUsers.ProxyAddresses carries a
    /// <c>'[]'::jsonb</c> default (Postgres cast syntax) that SQLite DDL rejects.
    /// The batch smoke test never touches ScannedUsers.
    /// </summary>
    private sealed class SqliteAppDbContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ScannedUser>()
                .Property(u => u.ProxyAddresses)
                .Metadata.SetDefaultValueSql(null);
        }
    }

    public MailboxBatchPersistenceTests()
    {
        // A single open in-memory connection keeps the schema alive across contexts.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new SqliteAppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Synced_batch_round_trips_through_the_database()
    {
        var source = new Tenant { DisplayName = "Source", Role = TenantRole.Source, TenantId = Guid.NewGuid().ToString() };
        var target = new Tenant { DisplayName = "Target", Role = TenantRole.Target, TenantId = Guid.NewGuid().ToString() };
        var project = new MigrationProject
        {
            Name = "Test",
            SourceTenantId = source.Id,
            TargetTenantId = target.Id,
        };
        var batch = new MailboxMigrationBatch
        {
            ProjectId = project.Id,
            Name = "Wave 1 mailboxes",
            Status = BatchStatus.Synced,
            Strategy = MailboxMigrationStrategy.NativeMrs,
        };

        using (var ctx = new SqliteAppDbContext(_options))
        {
            ctx.AddRange(source, target, project, batch);
            ctx.SaveChanges();
        }

        using (var ctx = new SqliteAppDbContext(_options))
        {
            var loaded = ctx.Set<MailboxMigrationBatch>().Single(b => b.Id == batch.Id);
            Assert.Equal(BatchStatus.Synced, loaded.Status);
            Assert.Equal(MailboxMigrationStrategy.NativeMrs, loaded.Strategy);
        }
    }

    [Fact]
    public void Synced_stays_appended_after_stopped_in_the_enum()
    {
        // Persisted integer values of pre-existing rows must stay valid: Synced
        // was deliberately appended last. Reordering the enum corrupts old rows.
        Assert.True((int)BatchStatus.Synced > (int)BatchStatus.Stopped,
            "BatchStatus.Synced must remain the last (appended) enum member.");
    }
}

/// <summary>
/// Mirrors the API's JSON options from Program.cs (camelCase properties +
/// camelCase string enums). The frontend's string-union types depend on these
/// exact wire values.
/// </summary>
public class EnumWireContractTests
{
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Theory]
    [InlineData(BatchStatus.Synced, "\"synced\"")]
    [InlineData(BatchStatus.Completing, "\"completing\"")]
    [InlineData(BatchStatus.Syncing, "\"syncing\"")]
    [InlineData(BatchStatus.Draft, "\"draft\"")]
    public void Batch_status_serializes_to_camel_case(BatchStatus status, string expected)
        => Assert.Equal(expected, JsonSerializer.Serialize(status, ApiJson));

    [Theory]
    [InlineData(MailboxMigrationStrategy.NativeMrs, "\"nativeMrs\"")]
    [InlineData(MailboxMigrationStrategy.GraphCopy, "\"graphCopy\"")]
    public void Strategy_serializes_to_camel_case(MailboxMigrationStrategy strategy, string expected)
        => Assert.Equal(expected, JsonSerializer.Serialize(strategy, ApiJson));
}
