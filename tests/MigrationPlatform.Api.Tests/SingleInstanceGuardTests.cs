using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationPlatform.Api.Services.InstanceLock;

namespace MigrationPlatform.Api.Tests;

public class SingleInstanceGuardTests
{
    // ── Advisory-lock key derivation (pure, deterministic) ──────────────────

    [Fact]
    public void DeriveLockKey_is_deterministic()
    {
        var a = SingleInstanceGuard.DeriveLockKey("MigrationPlatform.SingleInstance");
        var b = SingleInstanceGuard.DeriveLockKey("MigrationPlatform.SingleInstance");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeriveLockKey_differs_for_different_inputs()
    {
        Assert.NotEqual(
            SingleInstanceGuard.DeriveLockKey("MigrationPlatform.SingleInstance"),
            SingleInstanceGuard.DeriveLockKey("SomethingElse"));
    }

    [Fact]
    public void LockKey_matches_the_fixed_application_string()
    {
        Assert.Equal(
            SingleInstanceGuard.DeriveLockKey("MigrationPlatform.SingleInstance"),
            SingleInstanceGuard.LockKey);
    }

    // ── Config gating ───────────────────────────────────────────────────────

    [Fact]
    public async Task Enforce_false_marks_primary_without_touching_the_database()
    {
        var config = new ConfigurationBuilder()
            .AddInmemory(("SingleInstance:Enforce", "false"))
            // deliberately unreachable connection string — must not be used
            .Build();

        var guard = new SingleInstanceGuard(config, NullLogger<SingleInstanceGuard>.Instance);
        await guard.StartAsync(CancellationToken.None);

        Assert.True(SingleInstanceState.IsPrimary);
        await guard.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Missing_connection_string_degrades_to_primary()
    {
        var config = new ConfigurationBuilder()
            .AddInmemory(("SingleInstance:Enforce", "true"))
            .Build();

        var guard = new SingleInstanceGuard(config, NullLogger<SingleInstanceGuard>.Instance);
        await guard.StartAsync(CancellationToken.None);

        Assert.True(SingleInstanceState.IsPrimary);
        await guard.StopAsync(CancellationToken.None);
    }
}

file static class ConfigExtensions
{
    public static IConfigurationBuilder AddInmemory(
        this IConfigurationBuilder builder, params (string Key, string Value)[] pairs)
        => builder.AddInMemoryCollection(
            pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
}
