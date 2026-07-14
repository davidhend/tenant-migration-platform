using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MigrationPlatform.Api.Models;

namespace MigrationPlatform.Api.Data.Configuration;

internal sealed class ScannedDomainConfiguration : IEntityTypeConfiguration<ScannedDomain>
{
    public void Configure(EntityTypeBuilder<ScannedDomain> builder)
    {
        builder.ToTable("ScannedDomains");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(256);

        builder.HasOne<Scan>()
            .WithMany()
            .HasForeignKey(e => e.ScanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.ScanId);
    }
}
