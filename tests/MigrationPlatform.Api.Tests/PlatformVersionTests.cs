using System.Text.RegularExpressions;
using MigrationPlatform.Api.Services;
using Xunit;

namespace MigrationPlatform.Api.Tests;

public class PlatformVersionTests
{
    [Fact]
    public void Current_is_a_clean_semver_string()
    {
        var v = PlatformVersion.Current;
        Assert.False(string.IsNullOrWhiteSpace(v));
        Assert.DoesNotContain("+", v);                        // no build metadata suffix
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+"), v);      // semver-shaped
    }

    [Fact]
    public void RunbookVersion_matches_platform_version()
        => Assert.Equal(PlatformVersion.Current, PlatformVersion.RunbookVersion);
}
