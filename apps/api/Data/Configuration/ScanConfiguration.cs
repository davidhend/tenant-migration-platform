using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScanConfiguration : IEntityTypeConfiguration<Scan>
{
    public void Configure(EntityTypeBuilder<Scan> builder)
    {
        builder.ToTable("Scans");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ScanType)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(2048);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.StartedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.CompletedAt)
            .HasColumnType("timestamp with time zone");

        // ScanSummary is an owned type stored as flattened nullable columns
        builder.OwnsOne(e => e.Summary, summary =>
        {
            summary.Property(s => s.UserCount).HasColumnName("Summary_UserCount");
            summary.Property(s => s.GroupCount).HasColumnName("Summary_GroupCount");
            summary.Property(s => s.MailboxCount).HasColumnName("Summary_MailboxCount");
            summary.Property(s => s.MailboxTotalSizeGb).HasColumnName("Summary_MailboxTotalSizeGb");
            summary.Property(s => s.SiteCount).HasColumnName("Summary_SiteCount");
            summary.Property(s => s.OneDriveCount).HasColumnName("Summary_OneDriveCount");
            summary.Property(s => s.DomainCount).HasColumnName("Summary_DomainCount");
            summary.Property(s => s.BlockerCount).HasColumnName("Summary_BlockerCount");
            summary.Property(s => s.WarningCount).HasColumnName("Summary_WarningCount");
            summary.Property(s => s.ReadinessScore).HasColumnName("Summary_ReadinessScore");
        });

        // FK to Tenant — Restrict (cannot delete a tenant that has scans)
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK to MigrationProject — nullable, Restrict
        builder.HasOne<MigrationProject>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.TenantId);
        builder.HasIndex(e => e.ProjectId);
    }
}
