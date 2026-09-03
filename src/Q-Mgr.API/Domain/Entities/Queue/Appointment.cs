using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Queue;

/// <summary>
/// A scheduled booking — the "before you arrive" half of queue management that this product was
/// missing entirely. It is deliberately its own table rather than more columns on
/// <see cref="Token"/>: an appointment exists for days before any token does, most appointments
/// never become a token at all (cancelled/no-show), and a token's whole lifecycle is
/// same-day-and-serial while an appointment's is calendar-shaped. That is exactly the
/// "sub-resource with an independent lifecycle an existing row cannot represent" case the
/// project's enhance-before-you-add rule reserves a new table for. Nothing about the scheduling
/// *configuration* needed new schema, though — that lives in the existing Branch.Settings blob
/// (see AppointmentSettingsDto) and the existing Branch.OperatingHours column.
/// <para>
/// Inherits <see cref="BaseAuditableEntity"/> (like <see cref="ServiceType"/> and Branch) rather
/// than the bare <see cref="BaseEntity"/> Token uses — an appointment is edited by named people
/// over time, so CreatedBy/UpdatedBy earn their place here in a way they never did on a token.
/// </para>
/// <para>
/// Every DateTime on this entity is UTC. Npgsql maps these to <c>timestamptz</c>, which rejects a
/// DateTime whose Kind is not Utc, so every value assigned here goes through
/// <c>DateTime.SpecifyKind(x, DateTimeKind.Utc)</c> at the boundary.
/// </para>
/// </summary>
public class Appointment : BaseAuditableEntity
{
    /// <summary>
    /// Denormalised from Branch on purpose. Appointment has no global tenant query filter (like
    /// Token, Counter, Visitor and Feedback), so every query filters by BranchId after an explicit
    /// VerifyBranchOwnership — but the background jobs sweep across tenants with no branch in hand
    /// and need the owning organization to address a notification, and joining Branch on every row
    /// of that sweep to learn it would be pure overhead.
    /// </summary>
    public Guid OrganizationId { get; set; }

    public Guid BranchId { get; set; }
    public Guid ServiceTypeId { get; set; }

    /// <summary>
    /// Short code the customer is told to quote ("APT-7K3QF2"). Unique per branch — see
    /// AppointmentConfiguration for why the uniqueness lives here and not on (BranchId, ScheduledAt).
    /// </summary>
    public string ReferenceCode { get; set; } = string.Empty;

    // Customer — a walk-up booking has no user account, so these are plain fields rather than a
    // link to Users, exactly as Token models its customer.
    public string CustomerName { get; set; } = string.Empty;
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }

    /// <summary>Start of the appointment, UTC.</summary>
    public DateTime ScheduledAt { get; set; }

    /// <summary>Defaults from ServiceType.AverageServiceTimeMinutes when the caller does not say.</summary>
    public int DurationMinutes { get; set; } = 15;

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;

    public string? Notes { get; set; }

    // Integration surface — same two-field shape Token uses, so an inbound webhook or the
    // integration SDK can create an appointment under its own system's reference and look it up
    // again idempotently.
    public string? ExternalReference { get; set; }
    public string? ExternalSystem { get; set; }

    /// <summary>
    /// The live queue ticket this appointment became at check-in, if it has been checked in.
    /// Null for everything else — an appointment is not a token until its customer walks in.
    /// </summary>
    public Guid? TokenId { get; set; }

    /// <summary>Null for a booking made anonymously through the public page.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTime? CheckedInAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    /// <summary>
    /// When the pre-appointment reminder was actually sent. The recurring job filters on this
    /// being null, so a reminder can never go out twice — same no-new-table reasoning as
    /// WelfareRecord.ReminderSentAt.
    /// </summary>
    public DateTime? ReminderSentAt { get; set; }

    // Navigation properties. Configured with WithMany() (no inverse collection) because Branch,
    // ServiceType and Token are owned by other agents/files in this codebase and do not need to
    // grow an Appointments collection for any query here to work.
    public virtual Organization.Branch? Branch { get; set; }
    public virtual ServiceType? ServiceType { get; set; }
    public virtual Token? Token { get; set; }
}
