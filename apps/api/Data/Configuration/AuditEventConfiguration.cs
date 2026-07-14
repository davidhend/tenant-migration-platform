using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Timestamp)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.Actor)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.Action)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(e => e.Resource)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.Outcome)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(e => e.Details)
            .HasColumnType("jsonb");

        // FK to MigrationProject — nullable, SetNull so deleting a project does
        // not remove its audit history
        builder.HasOne<MigrationProject>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.ProjectId);
    }
}
