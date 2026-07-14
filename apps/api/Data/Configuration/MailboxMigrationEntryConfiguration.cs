using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class MailboxMigrationEntryConfiguration : IEntityTypeConfiguration<MailboxMigrationEntry>
{
    public void Configure(EntityTypeBuilder<MailboxMigrationEntry> builder)
    {
        builder.ToTable("MailboxMigrationEntries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceUpn)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.TargetUpn)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.MessagesCopied)
            .HasDefaultValue(0);

        builder.Property(e => e.TotalMessages)
            .HasDefaultValue(0);

        builder.Property(e => e.ErrorMessage)
            .HasMaxLength(2048);

        builder.Property(e => e.LastUpdated)
            .HasColumnType("timestamp with time zone");

        // FK to MailboxMigrationBatch — cascade so deleting a batch removes its entries
        builder.HasOne(e => e.Batch)
            .WithMany()
            .HasForeignKey(e => e.BatchId)
            .HasConstraintName("FK_MailboxEntries_Batch")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.BatchId);
    }
}
