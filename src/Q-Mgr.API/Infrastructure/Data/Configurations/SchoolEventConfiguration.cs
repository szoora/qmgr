using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Calendar;

namespace QMgr.Infrastructure.Data.Configurations;

/// <summary>School events (plan TERM_PROGRAMME_CALENDAR_AND_GATES, 2026-09-23). Tenant query filter in QMgrDbContext.</summary>
public class SchoolEventConfiguration : IEntityTypeConfiguration<SchoolEvent>
{
    public void Configure(EntityTypeBuilder<SchoolEvent> b)
    {
        b.ToTable("SchoolEvents");
        b.HasKey(e => e.Id);
        b.Property(e => e.Title).HasMaxLength(200).IsRequired();
        b.Property(e => e.Description).HasMaxLength(2000);
        b.Property(e => e.Category).HasMaxLength(60);
        b.Property(e => e.Location).HasMaxLength(200);
        b.Property(e => e.ResponsibleText).HasMaxLength(300);
        b.Property(e => e.SeriesName).HasMaxLength(200);
        b.Property(e => e.SourceKey).HasMaxLength(200);
        b.PrimitiveCollection(e => e.ClassNames).HasColumnType("text[]");
        b.PrimitiveCollection(e => e.ResponsibleUserIds).HasColumnType("uuid[]");
        b.PrimitiveCollection(e => e.ResponsibleDepartmentIds).HasColumnType("uuid[]");

        // "What is on between these dates" — the calendar page, My School Day, the feed and the signage zone.
        b.HasIndex(e => new { e.OrganizationId, e.StartsOn, e.EndsOn }).HasDatabaseName("idx_school_events_org_dates");
        b.HasIndex(e => e.ImportJobId).HasFilter("\"ImportJobId\" IS NOT NULL").HasDatabaseName("idx_school_events_import_job");
        b.HasIndex(e => new { e.OrganizationId, e.SourceKey }).HasFilter("\"SourceKey\" IS NOT NULL").HasDatabaseName("idx_school_events_source_key");
        b.HasIndex(e => e.DutyId).HasFilter("\"DutyId\" IS NOT NULL").HasDatabaseName("idx_school_events_duty");

        b.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Branch).WithMany().HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}
