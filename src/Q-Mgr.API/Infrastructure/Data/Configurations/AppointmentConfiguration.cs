using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QMgr.Domain.Entities.Queue;

namespace QMgr.Infrastructure.Data.Configurations;

/// <summary>
/// Picked up automatically by <c>ApplyConfigurationsFromAssembly</c> in QMgrDbContext — no
/// registration needed beyond the DbSet property.
/// </summary>
public class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("appointments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.ReferenceCode)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(a => a.CustomerName)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(a => a.CustomerPhone)
            .HasMaxLength(50);

        builder.Property(a => a.CustomerEmail)
            .HasMaxLength(255);

        builder.Property(a => a.Notes)
            .HasMaxLength(1000);

        builder.Property(a => a.ExternalReference)
            .HasMaxLength(100);

        builder.Property(a => a.ExternalSystem)
            .HasMaxLength(100);

        builder.Property(a => a.CancellationReason)
            .HasMaxLength(500);

        // ---- Indexes -----------------------------------------------------------------------
        //
        // (BranchId, ScheduledAt) is NOT unique, deliberately. The brief asked whether it could
        // be: it cannot, and making it so would be a live correctness bug rather than a
        // constraint. A branch legitimately serves several customers at the same instant — one
        // per counter, and one per service type running in parallel — so "two appointments at
        // 10:00 in this branch" is the normal case, not a duplicate. A unique index would also
        // turn every concurrent booking of the same popular start time into a 500 from the
        // database rather than a considered "that slot is full" answer.
        //
        // Slot capacity is therefore enforced where it can actually be reasoned about: in
        // AppointmentsController's booking path, which counts the non-terminal appointments
        // overlapping the requested slot for that ONE service type and compares against the
        // branch's configured CapacityPerSlot, all inside a transaction holding
        // pg_advisory_xact_lock keyed on branch+service-type+slot — the same concurrency pattern
        // TokenRepository.GetNextTokenNumberAsync and VisitorsController.GenerateBadgeCodeAsync
        // already use in this codebase. That serializes only bookings competing for the same
        // slot, and lets every other concurrent booking through untouched.
        //
        // The index itself still earns its place non-uniquely: every list, availability and
        // reminder query is "this branch, this time range".
        builder.HasIndex(a => new { a.BranchId, a.ScheduledAt })
            .HasDatabaseName("idx_appointments_branch_scheduled");

        builder.HasIndex(a => new { a.BranchId, a.Status })
            .HasDatabaseName("idx_appointments_branch_status");

        // The reference code IS safe to make unique per branch — it is generated, retried on
        // collision, and quoting it must identify exactly one booking.
        builder.HasIndex(a => new { a.BranchId, a.ReferenceCode })
            .IsUnique()
            .HasDatabaseName("idx_appointments_reference_unique");

        // Idempotent lookup for integrations, mirroring idx_tokens_external.
        builder.HasIndex(a => new { a.ExternalSystem, a.ExternalReference })
            .HasDatabaseName("idx_appointments_external");

        // Drives the reminder sweep's "due soon and not yet reminded" scan.
        builder.HasIndex(a => new { a.Status, a.ScheduledAt })
            .HasDatabaseName("idx_appointments_status_scheduled");

        // ---- Relationships -----------------------------------------------------------------
        // WithMany() with no inverse navigation: Branch, ServiceType and Token are not modified
        // by this feature, so none of them grows an Appointments collection.
        builder.HasOne(a => a.Branch)
            .WithMany()
            .HasForeignKey(a => a.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.ServiceType)
            .WithMany()
            .HasForeignKey(a => a.ServiceTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // SetNull, not Cascade: a token being purged must never take the appointment record with
        // it — the booking history is the thing the branch reports on.
        builder.HasOne(a => a.Token)
            .WithMany()
            .HasForeignKey(a => a.TokenId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
