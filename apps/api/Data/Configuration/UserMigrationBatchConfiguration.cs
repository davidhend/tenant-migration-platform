using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class UserMigrationBatchConfiguration : IEntityTypeConfiguration<UserMigrationBatch>
{
    public void Configure(EntityTypeBuilder<UserMigrationBatch> builder)
    {
        builder.ToTable("UserMigrationBatches");
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
            .HasDefaultValue(UserMigrationStrategy.DirectGraph);

        builder.Property(e => e.SkippedUsers)
            .HasDefaultValue(0);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(2048);

        builder.Property(e => e.CrossTenantSyncJobId)
            .HasMaxLength(256);

        builder.Property(e => e.CrossTenantSyncRuleId)
            .HasMaxLength(128);

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.StartedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.CompletedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.LastUpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.HasOne(e => e.Project)
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .HasConstraintName("FK_UserMigrationBatches_Project")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Wave)
            .WithMany(w => w.UserBatches)
            .HasForeignKey(e => e.WaveId)
            .HasConstraintName("FK_UserMigrationBatches_Wave")
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.WaveId);
    }
}
