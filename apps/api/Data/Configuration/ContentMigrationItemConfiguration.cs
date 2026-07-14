using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ContentMigrationItemConfiguration : IEntityTypeConfiguration<ContentMigrationItem>
{
    public void Configure(EntityTypeBuilder<ContentMigrationItem> builder)
    {
        builder.ToTable("ContentMigrationItems");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceUrl)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(e => e.TargetUrl)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(e => e.OwnerUpn)
            .HasMaxLength(512);

        builder.Property(e => e.TargetOwnerUpn)
            .HasMaxLength(512);

        builder.Property(e => e.SpoJobId)
            .HasMaxLength(256);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(2048);

        builder.Property(e => e.LastUpdated)
            .HasColumnType("timestamp with time zone");

        // FK to ContentMigrationJob — cascade so deleting a job removes its items
        builder.HasOne(e => e.Job)
            .WithMany()
            .HasForeignKey(e => e.JobId)
            .HasConstraintName("FK_ContentItems_Job")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.JobId);
    }
}
