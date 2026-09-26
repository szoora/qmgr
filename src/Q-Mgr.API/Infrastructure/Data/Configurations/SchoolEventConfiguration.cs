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
        b.PrimitiveCollection(e => e.AudienceStaffGroups).HasColumnType("text[]");
        b.PrimitiveCollection(e => e.AudienceRoleCodes).HasColumnType("text[]");
        b.PrimitiveCollection(e => e.AudienceDepartmentIds).HasColumnType("uuid[]");
        b.PrimitiveCollection(e => e.AudienceUserIds).HasColumnType("uuid[]");
        b.Property(e => e.AllStaff).HasDefaultValue(true);
        b.Property(e => e.RemindersOn).HasDefaultValue(true);
        b.Property(e => e.Version).HasDefaultValue(1);
        b.Property(e => e.CancelReason).HasMaxLength(300);
        b.Property(e => e.Recurrence).HasMaxLength(160);
        // xmin as the row version: Npgsql maps a uint row version to the system column, so nothing is added.
        b.Property(e => e.RowVersion).IsRowVersion();

        // A double press of "Add event" makes one event: the second insert fails this index and is answered with the first.
        b.HasIndex(e => new { e.OrganizationId, e.ClientRequestId }).IsUnique()
            .HasFilter("\"ClientRequestId\" IS NOT NULL").HasDatabaseName("ux_school_events_client_request");
        // The SourceKey uniqueness is a raw-SQL expression index in the migration (COALESCE on BranchId), which EF
        // cannot model — see 20260926_CalendarAudiencesAndImportRouting.

        // The meeting an event IS. SetNull, so a duty that is removed leaves an event, never a dangling id.
        b.HasOne<QMgr.Domain.Entities.Staff.StaffDuty>().WithMany().HasForeignKey(e => e.DutyId).OnDelete(DeleteBehavior.SetNull);

        // "What is on between these dates" — the calendar page, My School Day, the feed and the signage zone.
        b.HasIndex(e => new { e.OrganizationId, e.StartsOn, e.EndsOn }).HasDatabaseName("idx_school_events_org_dates");
        b.HasIndex(e => e.ImportJobId).HasFilter("\"ImportJobId\" IS NOT NULL").HasDatabaseName("idx_school_events_import_job");
        b.HasIndex(e => new { e.OrganizationId, e.SourceKey }).HasFilter("\"SourceKey\" IS NOT NULL").HasDatabaseName("idx_school_events_source_key");
        b.HasIndex(e => e.DutyId).HasFilter("\"DutyId\" IS NOT NULL").HasDatabaseName("idx_school_events_duty");

        b.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Branch).WithMany().HasForeignKey(e => e.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}
