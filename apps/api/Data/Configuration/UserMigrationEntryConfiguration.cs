using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class UserMigrationEntryConfiguration : IEntityTypeConfiguration<UserMigrationEntry>
{
    public void Configure(EntityTypeBuilder<UserMigrationEntry> builder)
    {
        builder.ToTable("UserMigrationEntries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceUpn)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.TargetUpn)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.TargetObjectId)
            .HasMaxLength(128);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(2048);

        builder.Property(e => e.LastUpdated)
            .HasColumnType("timestamp with time zone");

        builder.HasOne(e => e.Batch)
            .WithMany()
            .HasForeignKey(e => e.BatchId)
            .HasConstraintName("FK_UserMigrationEntries_Batch")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.BatchId);
    }
}
