using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class MailboxMigrationBatchConfiguration : IEntityTypeConfiguration<MailboxMigrationBatch>
{
    public void Configure(EntityTypeBuilder<MailboxMigrationBatch> builder)
    {
        builder.ToTable("MailboxMigrationBatches");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.Strategy)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(MailboxMigrationStrategy.GraphCopy);

        builder.Property(e => e.ExoMigrationBatchId)
            .HasMaxLength(128);

        builder.Property(e => e.TargetFolderName)
            .HasMaxLength(256);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(2048);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.StartedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.CompletedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.LastSyncedAt)
            .HasColumnType("timestamp with time zone");

        // FK to MigrationProject — cascade so deleting a project removes its batches
        builder.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .HasConstraintName("FK_MailboxBatches_Project")
            .OnDelete(DeleteBehavior.Cascade);

        // Optional FK to MigrationWave — set null when a wave is deleted
        builder.HasOne(e => e.Wave)
            .WithMany(w => w.MailboxBatches)
            .HasForeignKey(e => e.WaveId)
            .HasConstraintName("FK_MailboxBatches_Wave")
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.WaveId);
    }
}
