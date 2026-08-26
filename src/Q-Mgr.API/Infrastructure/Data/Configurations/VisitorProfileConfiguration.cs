using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Visitor;

namespace QMgr.Infrastructure.Data.Configurations;

public class VisitorProfileConfiguration : IEntityTypeConfiguration<VisitorProfile>
{
    public void Configure(EntityTypeBuilder<VisitorProfile> builder)
    {
        builder.ToTable("visitor_profiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.FullName).HasMaxLength(255).IsRequired();
        builder.Property(p => p.Phone).HasMaxLength(50);
        builder.Property(p => p.Email).HasMaxLength(255);
        builder.Property(p => p.Company).HasMaxLength(255);
        builder.Property(p => p.IdNumber).HasMaxLength(100);
        builder.Property(p => p.WatchlistReason).HasMaxLength(500);
        builder.Property(p => p.DeletionReason).HasMaxLength(500);

        builder.Property(p => p.NormalizedPhone).HasMaxLength(50);
        builder.Property(p => p.NormalizedEmail).HasMaxLength(255);
        builder.Property(p => p.NormalizedIdNumber).HasMaxLength(100);

        // Duplicate-prevention: within an organization, a given phone/email/ID can belong to
        // only one profile — this is the actual "no duplication" rule, enforced at the DB level
        // so a race between two concurrent check-ins can't create two profiles for the same
        // person. Deliberately org-scoped (not branch-scoped) so a person is recognized as the
        // same returning visitor at any branch of the org, and deliberately excludes soft-deleted
        // profiles so a corrected/merged-away profile doesn't permanently block the identifier.
        builder.HasIndex(p => new { p.OrganizationId, p.NormalizedEmail })
            .IsUnique()
            .HasFilter("\"NormalizedEmail\" IS NOT NULL AND \"DeletedAt\" IS NULL")
            .HasDatabaseName("idx_visitor_profiles_org_email_unique");

        builder.HasIndex(p => new { p.OrganizationId, p.NormalizedPhone })
            .IsUnique()
            .HasFilter("\"NormalizedPhone\" IS NOT NULL AND \"DeletedAt\" IS NULL")
            .HasDatabaseName("idx_visitor_profiles_org_phone_unique");

        builder.HasIndex(p => new { p.OrganizationId, p.NormalizedIdNumber })
            .IsUnique()
            .HasFilter("\"NormalizedIdNumber\" IS NOT NULL AND \"DeletedAt\" IS NULL")
            .HasDatabaseName("idx_visitor_profiles_org_id_unique");

        // Prefix search on name ("returning visitor" typeahead) — text_pattern_ops lets Postgres
        // use this btree index for a LIKE/ILIKE 'prefix%' query, which is what the search endpoint
        // issues; a plain btree index can't be used for pattern matching under most collations.
        builder.HasIndex(p => p.FullName)
            .HasDatabaseName("idx_visitor_profiles_name_prefix")
            .HasMethod("btree")
            .HasOperators("text_pattern_ops");

        builder.HasIndex(p => new { p.OrganizationId, p.DeletedAt })
            .HasDatabaseName("idx_visitor_profiles_org_deleted");

        builder.HasOne(p => p.Organization)
            .WithMany()
            .HasForeignKey(p => p.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
