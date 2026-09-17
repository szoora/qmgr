using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Welfare;

namespace QMgr.Infrastructure.Data.Configurations;

public class ClassTeacherAssignmentConfiguration : IEntityTypeConfiguration<ClassTeacherAssignment>
{
    public void Configure(EntityTypeBuilder<ClassTeacherAssignment> builder)
    {
        builder.HasKey(a => a.Id);

        // IsLive is a computed convenience over EndedAt — no column behind it, and mapping one
        // would let the two disagree (same reasoning as StudentFlag.IsActive).
        builder.Ignore(a => a.IsLive);

        builder.Property(a => a.ClassName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EndReason).HasMaxLength(500);

        // "Who teaches S4B?" — the notification fan-out, run once per welfare record created.
        // lower(ClassName) because Student.ClassName is free text and the vocabulary is matched
        // case-insensitively; an index on the raw column would not be used by that comparison.
        builder.HasIndex(a => new { a.BranchId, a.ClassName })
            .HasFilter("\"EndedAt\" IS NULL")
            .HasDatabaseName("idx_class_teacher_branch_class_live");

        // "Which classes am I?" — the scope filter, hit on essentially every roster and welfare
        // request made by a class-scoped user, so it must be an index seek and not a scan.
        builder.HasIndex(a => a.UserId)
            .HasFilter("\"EndedAt\" IS NULL")
            .HasDatabaseName("idx_class_teacher_user_live");

        // At most one LIVE class teacher (not assistant) per class. A partial unique index rather
        // than a check in code: ending an assignment frees the seat immediately with no
        // soft-delete dance, and two admins racing to assign the same class cannot both win.
        // Role = 0 is ClassTeacherRole.ClassTeacher — written as the stored int because the filter
        // is raw SQL. LOWER() to match the case-insensitive comparison used everywhere else.
        builder.HasIndex(a => new { a.BranchId, a.ClassName, a.Role })
            .IsUnique()
            .HasFilter("\"EndedAt\" IS NULL AND \"Role\" = 0")
            .HasDatabaseName("ux_class_teacher_one_primary_per_class");

        // The same teacher cannot hold the same subject in the same class twice (duty rota plan §3.2).
        // Role = 2 is ClassTeacherRole.SubjectTeacher, written as the stored int for the same reason as above.
        builder.HasIndex(a => new { a.BranchId, a.ClassName, a.UserId, a.SubjectId })
            .IsUnique()
            .HasFilter("\"EndedAt\" IS NULL AND \"Role\" = 2")
            .HasDatabaseName("ux_subject_teacher_once_per_class_subject");

        builder.HasOne(a => a.Subject)
            .WithMany()
            .HasForeignKey(a => a.SubjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Organization)
            .WithMany()
            .HasForeignKey(a => a.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Branch)
            .WithMany()
            .HasForeignKey(a => a.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not Cascade: deleting a user must not silently erase the record of which
        // classes they held and when — that history is the audit answer to "who could see this
        // child's file last term". Ending the assignment is the supported way to remove one.
        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
