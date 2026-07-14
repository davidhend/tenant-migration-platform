using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class DomainRuleConfiguration : IEntityTypeConfiguration<DomainRule>
{
    public void Configure(EntityTypeBuilder<DomainRule> builder)
    {
        builder.ToTable("DomainRules");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RuleType)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.SourcePattern)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.TargetPattern)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.Description)
            .HasMaxLength(1024);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        // FK to MigrationProject — cascade so deleting a project removes its rules
        builder.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .HasConstraintName("FK_DomainRules_Project")
            .OnDelete(DeleteBehavior.Cascade);

        // Composite index supports the ordered evaluation query:
        // WHERE ProjectId = ? AND IsEnabled = true ORDER BY Priority ASC
        builder.HasIndex(e => new { e.ProjectId, e.Priority });
    }
}
