using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScannedUserConfiguration : IEntityTypeConfiguration<ScannedUser>
{
    public void Configure(EntityTypeBuilder<ScannedUser> builder)
    {
        builder.ToTable("ScannedUsers");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceObjectId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.Upn)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(e => e.MailboxType)
            .HasMaxLength(64);

        // List<string> stored as a JSON array in PostgreSQL
        builder.Property(e => e.Licenses)
            .HasColumnType("jsonb");

        builder.Property(e => e.ProxyAddresses)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
