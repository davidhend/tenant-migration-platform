using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Shared in-memory-SQLite harness for repository/persistence integration tests.
/// One open connection keeps the schema alive across contexts. The only Npgsql
/// artifact that SQLite DDL rejects — <c>ScannedUsers.ProxyAddresses</c>'s
/// <c>'[]'::jsonb</c> default — is neutralised; nothing under test touches it.
/// </summary>
public sealed class SqliteHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    public DbContextOptions<AppDbContext> Options { get; }

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

    public SqliteHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public AppDbContext NewContext() => new SqliteAppDbContext(Options);

    /// <summary>Seed a source/target tenant pair + project; returns the project id.</summary>
    public (Guid projectId, Guid sourceId, Guid targetId) SeedProject(string name = "Test project")
    {
        var source = new Tenant { DisplayName = "Source", Role = TenantRole.Source, TenantId = Guid.NewGuid().ToString() };
        var target = new Tenant { DisplayName = "Target", Role = TenantRole.Target, TenantId = Guid.NewGuid().ToString() };
        var project = new MigrationProject { Name = name, SourceTenantId = source.Id, TargetTenantId = target.Id };

        using var ctx = NewContext();
        ctx.AddRange(source, target, project);
        ctx.SaveChanges();
        return (project.Id, source.Id, target.Id);
    }

    public void Dispose() => _connection.Dispose();
}
