using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class RosterImportJobConfiguration : IEntityTypeConfiguration<RosterImportJob>
{
    public void Configure(EntityTypeBuilder<RosterImportJob> builder)
    {
        builder.ToTable("roster_import_jobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.SourceFileName).HasMaxLength(255);
        builder.Property(j => j.Source).HasMaxLength(30);
        builder.Property(j => j.FailureReason).HasMaxLength(2000);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(j => j.RowsJson).HasColumnType("text");

        builder.HasIndex(j => new { j.BranchId, j.CreatedAt })
            .HasDatabaseName("idx_roster_import_jobs_branch_created");

        builder.HasOne(j => j.Organization)
            .WithMany()
            .HasForeignKey(j => j.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(j => j.Branch)
            .WithMany()
            .HasForeignKey(j => j.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
