using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class VisitorConfiguration : IEntityTypeConfiguration<Visitor>
{
    public void Configure(EntityTypeBuilder<Visitor> builder)
    {
        builder.ToTable("visitors");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.BadgeCode)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(v => v.FullName)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(v => v.Phone).HasMaxLength(50);
        builder.Property(v => v.Email).HasMaxLength(255);
        builder.Property(v => v.Company).HasMaxLength(255);
        builder.Property(v => v.IdNumber).HasMaxLength(100);
        builder.Property(v => v.Purpose).HasMaxLength(500);
        builder.Property(v => v.HostName).HasMaxLength(255);
        builder.Property(v => v.WatchlistReason).HasMaxLength(500);
        builder.Property(v => v.Notes).HasMaxLength(2000);

        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);

        // Badge codes are per-branch sequences (e.g. V-20260825-0001) — unique within a branch,
        // not globally, so two branches can both hand out "V-20260825-0001" on the same day.
        builder.HasIndex(v => new { v.BranchId, v.BadgeCode })
            .IsUnique()
            .HasDatabaseName("idx_visitors_branch_badge");

        builder.HasIndex(v => new { v.BranchId, v.Status })
            .HasDatabaseName("idx_visitors_branch_status");

        builder.HasIndex(v => new { v.BranchId, v.CheckedInAt })
            .HasDatabaseName("idx_visitors_branch_checkedin");

        builder.HasOne(v => v.Organization)
            .WithMany()
            .HasForeignKey(v => v.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.Branch)
            .WithMany()
            .HasForeignKey(v => v.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.HostUser)
            .WithMany()
            .HasForeignKey(v => v.HostUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
