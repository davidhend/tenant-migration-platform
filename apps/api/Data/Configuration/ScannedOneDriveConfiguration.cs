using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScannedOneDriveConfiguration : IEntityTypeConfiguration<ScannedOneDrive>
{
    public void Configure(EntityTypeBuilder<ScannedOneDrive> builder)
    {
        builder.ToTable("ScannedOneDrives");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OwnerUpn)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(e => e.OwnerDisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.DriveUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(e => e.LastModified)
            .HasColumnType("timestamp with time zone");

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
