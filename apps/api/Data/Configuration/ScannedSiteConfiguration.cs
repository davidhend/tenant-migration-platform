using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScannedSiteConfiguration : IEntityTypeConfiguration<ScannedSite>
{
    public void Configure(EntityTypeBuilder<ScannedSite> builder)
    {
        builder.ToTable("ScannedSites");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SiteUrl)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(e => e.Title)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.Template)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.LastActivityDate)
            .HasColumnType("timestamp with time zone");

        // List<string> stored as a JSON array in PostgreSQL
        builder.Property(e => e.Owners)
            .HasColumnType("jsonb");

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
