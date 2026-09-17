using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Audit;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;

namespace QMgr.Infrastructure.Data.Configurations;

// One IEntityTypeConfiguration per Staff Performance table, in one file (the MarketingConfigurations
// precedent). The welfare tables have none and are pure convention; the plan says not to copy that.
// Table names are PascalCase like the welfare five. Every FK to a user is Restrict: deleting a user
// must never erase what was recorded about them or by them.

public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("Departments");
        b.HasKey(d => d.Id);
        b.Property(d => d.Name).HasMaxLength(100).IsRequired();
        b.Property(d => d.Code).HasMaxLength(20).IsRequired();

        // One code per organization, live rows only, so a retired department's code can be reused.
        b.HasIndex(d => new { d.OrganizationId, d.Code })
            .IsUnique()
            .HasFilter("\"IsActive\" = true")
            .HasDatabaseName("ux_departments_org_code_active");
        b.HasIndex(d => d.HeadUserId).HasDatabaseName("idx_departments_head");

        b.HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(d => d.Branch).WithMany().HasForeignKey(d => d.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(d => d.Head).WithMany().HasForeignKey(d => d.HeadUserId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(d => d.DeputyHead).WithMany().HasForeignKey(d => d.DeputyHeadUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class PerformanceParameterConfiguration : IEntityTypeConfiguration<PerformanceParameter>
{
    public void Configure(EntityTypeBuilder<PerformanceParameter> b)
    {
        b.ToTable("PerformanceParameters");
        b.HasKey(p => p.Id);
        b.Property(p => p.Name).HasMaxLength(120).IsRequired();
        b.Property(p => p.Description).HasMaxLength(1000);
        b.Property(p => p.Purpose).HasMaxLength(300);
        b.Property(p => p.Color).HasMaxLength(9);
        b.Property(p => p.Weight).HasPrecision(6, 2);
        b.Property(p => p.RubricJson).HasColumnType("text");

        b.HasIndex(p => new { p.OrganizationId, p.SortOrder }).HasDatabaseName("idx_performance_parameters_org_sort");

        b.HasOne(p => p.Organization).WithMany().HasForeignKey(p => p.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class StaffDutyConfiguration : IEntityTypeConfiguration<StaffDuty>
{
    public void Configure(EntityTypeBuilder<StaffDuty> b)
    {
        b.ToTable("StaffDuties");
        b.HasKey(d => d.Id);
        b.Property(d => d.Title).HasMaxLength(200).IsRequired();
        b.Property(d => d.Description).HasMaxLength(2000);
        b.Property(d => d.Location).HasMaxLength(200);
        b.PrimitiveCollection(d => d.ExpectedUserIds).HasColumnType("uuid[]");
        b.PrimitiveCollection(d => d.RecorderUserIds).HasColumnType("uuid[]");
        b.HasIndex(d => new { d.Kind, d.StartsAt }).HasDatabaseName("idx_staff_duties_kind_start");

        // "What is on this week" and the two sweeps ("starts soon, not reminded", "ended, register not
        // taken") all read by branch and start time.
        b.HasIndex(d => new { d.BranchId, d.StartsAt }).HasDatabaseName("idx_staff_duties_branch_start");
        b.HasIndex(d => d.EndsAt).HasFilter("\"RegisterClosedAt\" IS NULL").HasDatabaseName("idx_staff_duties_register_open");

        b.HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(d => d.Branch).WithMany().HasForeignKey(d => d.BranchId).OnDelete(DeleteBehavior.Restrict);
        // Restrict: retiring a parameter must never erase the duties filed under it.
        b.HasOne(d => d.Parameter).WithMany().HasForeignKey(d => d.ParameterId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class StaffPerformanceRecordConfiguration : IEntityTypeConfiguration<StaffPerformanceRecord>
{
    public void Configure(EntityTypeBuilder<StaffPerformanceRecord> b)
    {
        b.ToTable("StaffPerformanceRecords");
        b.HasKey(r => r.Id);
        b.Property(r => r.Description).HasMaxLength(2000).IsRequired();

        // The timeline: one person, newest first. The scoring pass: one branch, one period.
        b.HasIndex(r => new { r.SubjectUserId, r.OccurredAt }).HasDatabaseName("idx_staff_records_subject_occurred");
        b.HasIndex(r => new { r.BranchId, r.OccurredAt }).HasDatabaseName("idx_staff_records_branch_occurred");
        b.HasIndex(r => r.ParameterId).HasDatabaseName("idx_staff_records_parameter");
        b.HasIndex(r => r.DutyId).HasDatabaseName("idx_staff_records_duty");
        b.HasIndex(r => r.LoggedByUserId).HasDatabaseName("idx_staff_records_logged_by");

        b.HasOne(r => r.Organization).WithMany().HasForeignKey(r => r.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.Branch).WithMany().HasForeignKey(r => r.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.Subject).WithMany().HasForeignKey(r => r.SubjectUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.Parameter).WithMany(p => p.Records).HasForeignKey(r => r.ParameterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.Duty).WithMany(d => d.Records).HasForeignKey(r => r.DutyId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class StaffPerformanceNoteConfiguration : IEntityTypeConfiguration<StaffPerformanceNote>
{
    public void Configure(EntityTypeBuilder<StaffPerformanceNote> b)
    {
        b.ToTable("StaffPerformanceNotes");
        b.HasKey(n => n.Id);
        b.Property(n => n.Body).HasMaxLength(4000).IsRequired();
        b.HasIndex(n => n.RecordId).HasDatabaseName("idx_staff_notes_record");
        b.HasOne(n => n.Record).WithMany(r => r.Notes).HasForeignKey(n => n.RecordId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class StaffPerformanceAttachmentConfiguration : IEntityTypeConfiguration<StaffPerformanceAttachment>
{
    public void Configure(EntityTypeBuilder<StaffPerformanceAttachment> b)
    {
        b.ToTable("StaffPerformanceAttachments");
        b.HasKey(a => a.Id);
        b.Property(a => a.FileUrl).HasMaxLength(500).IsRequired();
        b.Property(a => a.FileName).HasMaxLength(255).IsRequired();
        b.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        b.HasIndex(a => a.RecordId).HasDatabaseName("idx_staff_attachments_record");
        // UploadAuthorizer looks a file up by the tail of FileUrl on every gated fetch.
        b.HasIndex(a => a.FileUrl).HasDatabaseName("idx_staff_attachments_file_url");
        b.HasOne(a => a.Record).WithMany(r => r.Attachments).HasForeignKey(a => a.RecordId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class StaffAppraisalConfiguration : IEntityTypeConfiguration<StaffAppraisal>
{
    public void Configure(EntityTypeBuilder<StaffAppraisal> b)
    {
        b.ToTable("StaffAppraisals");
        b.HasKey(a => a.Id);
        b.Property(a => a.PeriodKey).HasMaxLength(20).IsRequired();
        b.Property(a => a.ComputedScore).HasPrecision(6, 2);
        b.Property(a => a.SelfComments).HasMaxLength(4000);
        b.Property(a => a.AppraiserComments).HasMaxLength(4000);
        b.Property(a => a.Strengths).HasMaxLength(2000);
        b.Property(a => a.DevelopmentAreas).HasMaxLength(2000);
        b.Property(a => a.ModerationReason).HasMaxLength(1000);
        b.Property(a => a.AppealNote).HasMaxLength(2000);

        // One appraisal per person per period. A partial unique index rather than a check in code,
        // so two approvers opening a period at once cannot both create one.
        b.HasIndex(a => new { a.SubjectUserId, a.PeriodKey })
            .IsUnique()
            .HasDatabaseName("ux_staff_appraisals_subject_period");
        b.HasIndex(a => new { a.BranchId, a.PeriodKey, a.Stage }).HasDatabaseName("idx_staff_appraisals_branch_period_stage");
        b.HasIndex(a => a.AppraiserUserId).HasDatabaseName("idx_staff_appraisals_appraiser");

        b.HasOne(a => a.Organization).WithMany().HasForeignKey(a => a.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(a => a.Branch).WithMany().HasForeignKey(a => a.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(a => a.Subject).WithMany().HasForeignKey(a => a.SubjectUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(a => a.Appraiser).WithMany().HasForeignKey(a => a.AppraiserUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class StaffNoticeConfiguration : IEntityTypeConfiguration<StaffNotice>
{
    public void Configure(EntityTypeBuilder<StaffNotice> b)
    {
        b.ToTable("StaffNotices");
        b.HasKey(n => n.Id);
        b.Property(n => n.Title).HasMaxLength(200).IsRequired();
        b.Property(n => n.BodyHtml).HasColumnType("text").IsRequired();
        b.Property(n => n.Acknowledgements).HasColumnType("jsonb").IsRequired();
        b.PrimitiveCollection(n => n.AudienceDepartmentIds).HasColumnType("uuid[]");
        b.PrimitiveCollection(n => n.AudienceRoleCodes).HasColumnType("text[]");
        b.PrimitiveCollection(n => n.AttachmentMediaContentIds).HasColumnType("uuid[]");

        b.HasIndex(n => new { n.OrganizationId, n.PublishAt }).HasDatabaseName("idx_staff_notices_org_publish");

        b.HasOne(n => n.Organization).WithMany().HasForeignKey(n => n.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(n => n.Branch).WithMany().HasForeignKey(n => n.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> b)
    {
        b.ToTable("ActivityEvents");
        b.HasKey(e => e.Id);
        b.Property(e => e.Action).HasMaxLength(80).IsRequired();
        b.Property(e => e.EntityType).HasMaxLength(60).IsRequired();
        b.Property(e => e.Summary).HasMaxLength(500).IsRequired();
        b.Property(e => e.IpAddress).HasMaxLength(64);
        b.Property(e => e.UserAgent).HasMaxLength(120);
        b.Property(e => e.DetailJson).HasColumnType("text");

        // The three ways it is read: the branch log, one person's file (as subject), one actor's trail.
        b.HasIndex(e => new { e.OrganizationId, e.OccurredAt }).HasDatabaseName("idx_activity_org_occurred");
        b.HasIndex(e => new { e.SubjectUserId, e.OccurredAt }).HasDatabaseName("idx_activity_subject_occurred");
        b.HasIndex(e => new { e.ActorUserId, e.OccurredAt }).HasDatabaseName("idx_activity_actor_occurred");
        // The retention purge: rows past the window that still carry attribution.
        b.HasIndex(e => e.OccurredAt).HasFilter("\"IpAddress\" IS NOT NULL OR \"UserAgent\" IS NOT NULL").HasDatabaseName("idx_activity_attribution_pending");
    }
}

/// <summary>Subjects (duty rota plan §3.2). One live code per organization, so a retired subject's code can be reused.</summary>
public class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    public void Configure(EntityTypeBuilder<Subject> b)
    {
        b.ToTable("Subjects");
        b.HasKey(s => s.Id);
        b.Property(s => s.Name).HasMaxLength(100).IsRequired();
        b.Property(s => s.Code).HasMaxLength(20).IsRequired();
        b.Property(s => s.Color).HasMaxLength(9);

        b.HasIndex(s => new { s.OrganizationId, s.Code })
            .IsUnique()
            .HasFilter("\"IsActive\" = true")
            .HasDatabaseName("ux_subjects_org_code_active");
        b.HasIndex(s => s.DepartmentId).HasDatabaseName("idx_subjects_department");

        b.HasOne(s => s.Organization).WithMany().HasForeignKey(s => s.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.Department).WithMany().HasForeignKey(s => s.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}
