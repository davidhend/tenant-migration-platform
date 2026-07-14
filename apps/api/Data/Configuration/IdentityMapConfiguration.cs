using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class IdentityMapConfiguration : IEntityTypeConfiguration<IdentityMap>
{
    public void Configure(EntityTypeBuilder<IdentityMap> builder)
    {
        builder.ToTable("IdentityMaps");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceUpn)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(e => e.TargetUpn)
            .HasMaxLength(320);

        builder.Property(e => e.ConflictReason)
            .HasMaxLength(1024);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.MappingSource)
            .HasConversion<string>()
            .HasMaxLength(32);

        // FK to MigrationProject — Cascade so deleting a project removes its maps
        builder.HasOne<MigrationProject>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ProjectId);
    }
}
