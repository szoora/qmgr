using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class StudentGuardianConfiguration : IEntityTypeConfiguration<StudentGuardian>
{
    public void Configure(EntityTypeBuilder<StudentGuardian> builder)
    {
        builder.ToTable("student_guardians");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.Relationship).HasMaxLength(100);

        // Re-importing the same (student, guardian) pair — the common case when a roster sync
        // runs again — should update, not duplicate.
        builder.HasIndex(g => new { g.StudentId, g.VisitorProfileId })
            .IsUnique()
            .HasDatabaseName("idx_student_guardians_unique_pair");

        builder.HasIndex(g => g.VisitorProfileId)
            .HasDatabaseName("idx_student_guardians_profile");

        builder.HasOne(g => g.Student)
            .WithMany(s => s.Guardians)
            .HasForeignKey(g => g.StudentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(g => g.VisitorProfile)
            .WithMany()
            .HasForeignKey(g => g.VisitorProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
