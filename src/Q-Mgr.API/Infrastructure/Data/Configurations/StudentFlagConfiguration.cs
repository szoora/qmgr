using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Welfare;

namespace QMgr.Infrastructure.Data.Configurations;

public class StudentFlagConfiguration : IEntityTypeConfiguration<StudentFlag>
{
    public void Configure(EntityTypeBuilder<StudentFlag> builder)
    {
        builder.HasKey(f => f.Id);

        // IsActive is a computed convenience over EndedAt — there is no column behind it, and
        // mapping one would let the two disagree.
        builder.Ignore(f => f.IsActive);

        // The query every student page runs: "live flags for this student". Filtered on
        // EndedAt IS NULL so an ended flag doesn't bloat the index a lookup walks.
        builder.HasIndex(f => new { f.StudentId, f.EndedAt })
            .HasDatabaseName("idx_student_flags_student_active");

        // The overdue-review sweep's access path — mirrors how WelfareReminderJob finds overdue
        // actions, so a flag nobody has revisited surfaces the same way an ignored action does.
        builder.HasIndex(f => new { f.BranchId, f.ReviewDueDate })
            .HasFilter("\"EndedAt\" IS NULL")
            .HasDatabaseName("idx_student_flags_review_due");

        builder.HasOne(f => f.Organization)
            .WithMany()
            .HasForeignKey(f => f.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(f => f.Branch)
            .WithMany()
            .HasForeignKey(f => f.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(f => f.Student)
            .WithMany(s => s.Flags)
            .HasForeignKey(f => f.StudentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: deleting a category must never silently erase the safeguarding
        // flags filed under it. The category editor already blocks deletion of a category in use.
        builder.HasOne(f => f.Category)
            .WithMany()
            .HasForeignKey(f => f.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
