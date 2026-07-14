using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScannedGroupConfiguration : IEntityTypeConfiguration<ScannedGroup>
{
    public void Configure(EntityTypeBuilder<ScannedGroup> builder)
    {
        builder.ToTable("ScannedGroups");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceObjectId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.GroupType)
            .IsRequired()
            .HasMaxLength(64);

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
