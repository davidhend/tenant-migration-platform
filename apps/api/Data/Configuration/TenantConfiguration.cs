using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.TenantId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.AppClientId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.ClientSecretHint)
            .HasMaxLength(8);

        builder.Property(e => e.Role)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.AuthMethod)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.ConnectionStatus)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.LastVerifiedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        // Certificate fields — stored when AuthMethod == Certificate.
        // Base64 payload can be large (PFX with chain), so no hard MaxLength here.
        //
        // PRODUCTION NOTE: When KeyVault:Enabled=true the application writes
        // certificate bytes and the client secret exclusively to Azure Key Vault
        // (via IKeyVaultCredentialService). In that mode TenantsController passes
        // null for ClientCertificateBase64 and ClientCertificatePassword so these
        // columns remain null. The columns exist as a dev/staging fallback only.
        // ClientCertificateThumbprint is always stored here for display/auditing.
        builder.Property(e => e.ClientCertificateBase64)
            .HasColumnName("ClientCertificateBase64");

        builder.Property(e => e.ClientCertificateThumbprint)
            .HasMaxLength(64)
            .HasColumnName("ClientCertificateThumbprint");

        builder.Property(e => e.ClientCertificatePassword)
            .HasColumnName("ClientCertificatePassword");

        // ClientSecretPlain is a runtime-only property — never persisted
        builder.Ignore(e => e.ClientSecretPlain);

        // A TenantId string must be unique across all registered tenants
        builder.HasIndex(e => e.TenantId).IsUnique();
    }
}
