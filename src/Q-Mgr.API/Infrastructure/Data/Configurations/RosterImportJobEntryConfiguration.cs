using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class RosterImportJobEntryConfiguration : IEntityTypeConfiguration<RosterImportJobEntry>
{
    public void Configure(EntityTypeBuilder<RosterImportJobEntry> builder)
    {
        builder.ToTable("roster_import_job_entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.StudentCode).HasMaxLength(100);
        builder.Property(e => e.StudentName).HasMaxLength(255);
        builder.Property(e => e.GuardianName).HasMaxLength(255);
        builder.Property(e => e.Message).HasMaxLength(1000);
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(30);

        builder.HasIndex(e => new { e.RosterImportJobId, e.RowNumber })
            .HasDatabaseName("idx_roster_import_job_entries_job_row");

        builder.HasOne(e => e.RosterImportJob)
            .WithMany(j => j.Entries)
            .HasForeignKey(e => e.RosterImportJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
