using MigrationPlatform.Api.Services.Spo;

namespace MigrationPlatform.Api.Tests;

public class SpoCompatibilityStatusTests
{
    [Theory]
    [InlineData("Compatible")]
    [InlineData("compatible")]
    [InlineData("Warning")]
    [InlineData("  Compatible  ")]
    public void Compatible_and_warning_pass(string status)
        => Assert.True(SpoRestClient.IsCompatibleStatus(status));

    // THE regression test: "Incompatible" contains the substring "Compatible";
    // the original Contains() check green-lit broken tenant relationships.
    [Theory]
    [InlineData("Incompatible")]
    [InlineData("incompatible")]
    [InlineData("NotEstablished")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Anything_else_fails(string status)
        => Assert.False(SpoRestClient.IsCompatibleStatus(status));

    [Theory]
    [InlineData("@{Foo=Bar; CompatibilityStatus=Compatible}", "Compatible")]
    [InlineData("@{CompatibilityStatus=Incompatible; X=1}", "Incompatible")]
    [InlineData("@{CompatibilityStatus=Warning}", "Warning")]
    [InlineData("Compatible", "Compatible")]
    [InlineData("  Compatible ", "Compatible")]
    public void Legacy_powershell_stringification_is_extracted(string raw, string expected)
        => Assert.Equal(expected, SpoRestClient.NormalizeCompatibilityStatus(raw));

    [Fact]
    public void Legacy_incompatible_stringification_fails_preflight()
        => Assert.False(SpoRestClient.IsCompatibleStatus("@{CompatibilityStatus=Incompatible}"));
}
