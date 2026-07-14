using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ValidationCheckConfiguration : IEntityTypeConfiguration<ValidationCheck>
{
    public void Configure(EntityTypeBuilder<ValidationCheck> builder)
    {
        builder.ToTable("ValidationChecks");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.CheckType)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.Outcome)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(e => e.SourceReference)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.TargetReference)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(1024);

        builder.Property(e => e.CheckedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasOne(e => e.Run)
            .WithMany(r => r.Checks)
            .HasForeignKey(e => e.RunId)
            .HasConstraintName("FK_ValidationChecks_Run")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.RunId);
    }
}
