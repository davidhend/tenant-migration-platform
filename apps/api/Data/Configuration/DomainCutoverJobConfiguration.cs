using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class DomainCutoverJobConfiguration : IEntityTypeConfiguration<DomainCutoverJob>
{
    public void Configure(EntityTypeBuilder<DomainCutoverJob> builder)
    {
        builder.ToTable("DomainCutoverJobs");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DomainName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.Phase)
            .HasConversion<string>()
            .HasMaxLength(64);

        builder.Property(e => e.DnsVerificationRecord)
            .HasMaxLength(512);

        builder.Property(e => e.TargetMxRecord)
            .HasMaxLength(512);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(4096);

        builder.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.LastUpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .HasConstraintName("FK_DomainCutover_Project")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ProjectId);
    }
}
