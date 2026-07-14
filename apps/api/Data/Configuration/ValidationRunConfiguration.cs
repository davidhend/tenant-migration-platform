using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ValidationRunConfiguration : IEntityTypeConfiguration<ValidationRun>
{
    public void Configure(EntityTypeBuilder<ValidationRun> builder)
    {
        builder.ToTable("ValidationRuns");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(256);

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

        builder.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .HasConstraintName("FK_ValidationRuns_Project")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.WaveId);
    }
}
