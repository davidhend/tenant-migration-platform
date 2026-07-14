namespace MigrationPlatform.Api.Data;

/// <summary>
/// Placeholder seeder — no demo data is inserted.
/// Replace SeedAsync with real bootstrap data (e.g. default admin user) as needed.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(AppDbContext _, ILogger<DatabaseSeeder> logger)
    {
        _logger = logger;
    }

    public Task SeedAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("DatabaseSeeder: no seed data configured.");
        return Task.CompletedTask;
    }
}
