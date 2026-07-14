using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class LocalCredentialConfiguration : IEntityTypeConfiguration<LocalCredential>
{
    public void Configure(EntityTypeBuilder<LocalCredential> builder)
    {
        builder.ToTable("LocalCredentials");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UserName)
            .IsRequired()
            .HasMaxLength(256);
        builder.HasIndex(c => c.UserName).IsUnique();

        builder.Property(c => c.PasswordHash)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(c => c.CreatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(c => c.PasswordChangedAt)
            .HasColumnType("timestamp with time zone");
    }
}
