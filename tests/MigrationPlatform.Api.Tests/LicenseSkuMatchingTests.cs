using MigrationPlatform.Api.Services.Graph;

namespace MigrationPlatform.Api.Tests;

public class CrossTenantMigrationSkuMatchingTests
{
    [Theory]
    [InlineData("CROSSTENANTUSERDATAMIGRATION")]
    [InlineData("Cross_tenant_user_data_migration")]
    [InlineData("cross_tenant_user_data_migration")]
    [InlineData("Cross-Tenant-User-Data-Migration")]
    [InlineData("CSP_CROSSTENANTUSERDATAMIGRATION")]      // CSP-prefixed variant
    [InlineData("CROSSTENANTUSERDATAMIGRATION_FACULTY")]  // suffixed variant
    public void Matches_all_known_part_number_variants(string partNumber)
        => Assert.True(LicenseCheckService.IsCrossTenantMigrationSku(partNumber));

    [Theory]
    [InlineData("ENTERPRISEPACK")]           // E3
    [InlineData("SPE_E5")]
    [InlineData("EXCHANGESTANDARD")]
    [InlineData("CROSS_TENANT")]             // partial — not the migration SKU
    [InlineData("USER_DATA_MIGRATION")]      // partial
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_match_unrelated_skus(string? partNumber)
        => Assert.False(LicenseCheckService.IsCrossTenantMigrationSku(partNumber));

    [Theory]
    [InlineData("Cross_Tenant-User Data.Migration", "CROSSTENANTUSERDATAMIGRATION")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalization_strips_separators_and_uppercases(string? raw, string expected)
        => Assert.Equal(expected, LicenseCheckService.NormalizeSkuPart(raw));
}
