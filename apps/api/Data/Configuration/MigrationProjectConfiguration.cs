using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class MigrationProjectConfiguration : IEntityTypeConfiguration<MigrationProject>
{
    public void Configure(EntityTypeBuilder<MigrationProject> builder)
    {
        builder.ToTable("Projects");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        // Source tenant FK — named constraint, Restrict delete so deleting a tenant
        // that is referenced by a project is blocked at the database level.
        builder.HasOne(e => e.SourceTenant)
            .WithMany()
            .HasForeignKey(e => e.SourceTenantId)
            .HasConstraintName("FK_Projects_SourceTenant")
            .OnDelete(DeleteBehavior.Restrict);

        // Target tenant FK — same pattern
        builder.HasOne(e => e.TargetTenant)
            .WithMany()
            .HasForeignKey(e => e.TargetTenantId)
            .HasConstraintName("FK_Projects_TargetTenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
