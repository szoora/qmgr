using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class VisitorPassConfiguration : IEntityTypeConfiguration<VisitorPass>
{
    public void Configure(EntityTypeBuilder<VisitorPass> builder)
    {
        builder.ToTable("visitor_passes");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Label).HasMaxLength(255).IsRequired();
        builder.Property(p => p.TokenId).HasMaxLength(100).IsRequired();

        builder.HasIndex(p => p.TokenId)
            .IsUnique()
            .HasDatabaseName("idx_visitor_passes_token");

        builder.HasIndex(p => new { p.BranchId, p.RevokedAt, p.ExpiresAt })
            .HasDatabaseName("idx_visitor_passes_branch_active");

        builder.HasOne(p => p.Organization)
            .WithMany()
            .HasForeignKey(p => p.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Branch)
            .WithMany()
            .HasForeignKey(p => p.BranchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
