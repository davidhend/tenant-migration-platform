using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScannedMailboxConfiguration : IEntityTypeConfiguration<ScannedMailbox>
{
    public void Configure(EntityTypeBuilder<ScannedMailbox> builder)
    {
        builder.ToTable("ScannedMailboxes");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DisplayName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.PrimarySmtpAddress)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(e => e.MailboxType)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.LastLogonTime)
            .HasColumnType("timestamp with time zone");

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
