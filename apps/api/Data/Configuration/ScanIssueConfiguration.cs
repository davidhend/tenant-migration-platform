using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScanIssueConfiguration : IEntityTypeConfiguration<ScanIssue>
{
    public void Configure(EntityTypeBuilder<ScanIssue> builder)
    {
        builder.ToTable("ScanIssues");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Severity)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.Category)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.Code)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.Title)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.Description)
            .IsRequired();

        // List<string> stored as a JSON array in PostgreSQL
        builder.Property(e => e.RemediationSteps)
            .HasColumnType("jsonb");

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
