using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> builder)
    {
        builder.ToTable("students");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.FullName).HasMaxLength(255).IsRequired();
        builder.Property(s => s.StudentCode).HasMaxLength(100);
        builder.Property(s => s.ClassName).HasMaxLength(100);

        // A roster import upserts by StudentCode, so it needs to be a real key: unique per
        // organization when present, excluding soft-deactivated rows so a re-issued admission
        // number (a genuinely re-enrolled student) doesn't permanently collide with a stale one.
        builder.HasIndex(s => new { s.OrganizationId, s.StudentCode })
            .IsUnique()
            .HasFilter("\"StudentCode\" IS NOT NULL AND \"IsActive\" = true")
            .HasDatabaseName("idx_students_org_code_unique");

        builder.HasIndex(s => new { s.BranchId, s.IsActive })
            .HasDatabaseName("idx_students_branch_active");

        builder.HasOne(s => s.Organization)
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Branch)
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
