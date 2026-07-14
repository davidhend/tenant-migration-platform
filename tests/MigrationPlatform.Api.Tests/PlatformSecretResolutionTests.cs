using Microsoft.Extensions.Configuration;
using MigrationPlatform.Api.Services.KeyVault;
using NSubstitute;
using Xunit;

namespace MigrationPlatform.Api.Tests;

/// <summary>
/// Guards the platform secret plumbing: the config-path → store-name mapping
/// (Key Vault naming rules) and the resolver's precedence (real config value →
/// store; kv: markers are redirects, never returned as secrets).
/// </summary>
public class PlatformSecretNamesTests
{
    [Theory]
    [InlineData("Platform:CrossTenantMigration:ClientSecret", "platform-cross-tenant-migration-client-secret")]
    [InlineData("Azure:Identity:ClientSecret",                "platform-azure-identity-client-secret")]
    [InlineData("Azure:Identity:CertificateBase64",           "platform-azure-identity-certificate")]
    [InlineData("Azure:Identity:CertificatePassword",         "platform-azure-identity-certificate-password")]
    public void Maps_every_managed_config_path(string configPath, string expected) =>
        Assert.Equal(expected, PlatformSecretNames.ForConfigPath(configPath));

    [Fact]
    public void Unknown_config_path_throws() =>
        Assert.Throws<ArgumentException>(() => PlatformSecretNames.ForConfigPath("Jwt:SecretKey"));

    [Theory]
    [InlineData("kv:platform-azure-identity-client-secret", true)]
    [InlineData("KV:UPPERCASED-MARKER", true)]
    [InlineData("an-actual-secret-value", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Marker_detection(string? value, bool expected) =>
        Assert.Equal(expected, PlatformSecretNames.IsMarker(value));

    [Fact]
    public void Store_names_satisfy_key_vault_constraints()
    {
        foreach (var name in PlatformSecretNames.ByConfigPath.Values)
        {
            Assert.InRange(name.Length, 1, 127);
            Assert.All(name, c => Assert.True(char.IsLetterOrDigit(c) || c == '-',
                $"'{name}' contains '{c}', not allowed in Key Vault secret names."));
        }
    }
}

public class PlatformSecretResolverTests
{
    private const string Path = "Platform:CrossTenantMigration:ClientSecret";

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [Path] = value }).Build();

    [Fact]
    public async Task Real_config_value_wins_over_store()
    {
        var store = Substitute.For<IPlatformSecretStore>();
        var resolver = new PlatformSecretResolver(Config("from-config"), store);

        Assert.Equal("from-config", await resolver.GetAsync(Path));
        await store.DidNotReceiveWithAnyArgs().GetSecretAsync(default!, default);
    }

    [Theory]
    [InlineData("kv:platform-cross-tenant-migration-client-secret")] // marker → redirect
    [InlineData(null)]                                               // unset → store
    [InlineData("")]                                                 // empty → store
    public async Task Marker_or_missing_config_reads_the_store(string? configValue)
    {
        var store = Substitute.For<IPlatformSecretStore>();
        store.GetSecretAsync("platform-cross-tenant-migration-client-secret", Arg.Any<CancellationToken>())
            .Returns("from-store");
        var resolver = new PlatformSecretResolver(Config(configValue), store);

        Assert.Equal("from-store", await resolver.GetAsync(Path));
    }

    [Fact]
    public async Task Store_lookups_are_cached_until_invalidated()
    {
        var store = Substitute.For<IPlatformSecretStore>();
        store.GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("v1");
        var resolver = new PlatformSecretResolver(Config(null), store);

        Assert.Equal("v1", await resolver.GetAsync(Path));
        Assert.Equal("v1", await resolver.GetAsync(Path));
        await store.Received(1).GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        store.GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("v2");
        resolver.Invalidate(Path);
        Assert.Equal("v2", await resolver.GetAsync(Path));
        await store.Received(2).GetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
