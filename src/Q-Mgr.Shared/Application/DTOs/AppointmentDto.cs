using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

/// <summary>
/// Wire shape of one appointment, shared by Q-Mgr.API and Q-Mgr.Web rather than duplicated on
/// each side (see CLAUDE.md's standing note about DTO drift in this codebase — every previous
/// API/Web pair that kept its own copy eventually disagreed about a field name).
/// <para>
/// Every DateTime here is UTC. The Web layer converts to the branch's local time for display;
/// nothing anywhere stores or transmits a local timestamp.
/// </para>
/// </summary>
public record AppointmentDto
{
    public Guid Id { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid BranchId { get; init; }
    public Guid ServiceTypeId { get; init; }

    /// <summary>Short human-quotable code the customer is given ("APT-7K3QF2"). Unique per branch.</summary>
    public string ReferenceCode { get; init; } = string.Empty;

    public string CustomerName { get; init; } = string.Empty;
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }

    public DateTime ScheduledAt { get; init; }
    public int DurationMinutes { get; init; }
    public DateTime ScheduledEndAt => ScheduledAt.AddMinutes(DurationMinutes);

    public AppointmentStatus Status { get; init; }
    public string? Notes { get; init; }

    public string? ExternalReference { get; init; }
    public string? ExternalSystem { get; init; }

    /// <summary>Set once the appointment has been checked in and converted into a live queue ticket.</summary>
    public Guid? TokenId { get; init; }
    /// <summary>The issued token's display number ("GY-014"), when one exists — convenience for the admin list.</summary>
    public string? TokenDisplayNumber { get; init; }

    public Guid? CreatedByUserId { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancellationReason { get; init; }
    public DateTime? ReminderSentAt { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>Denormalised for list rendering — the admin grid would otherwise need a second call per row.</summary>
    public string? ServiceTypeName { get; init; }
    public string? ServiceTypeCode { get; init; }
    public string? ServiceTypeColor { get; init; }
}

/// <summary>
/// One bookable (or already-taken) slot. Free/busy only — never any other customer's details,
/// since this is served anonymously to the public booking page.
/// </summary>
public record AppointmentSlotDto
{
    public DateTime StartAt { get; init; }
    public DateTime EndAt { get; init; }
    public bool IsAvailable { get; init; }
    /// <summary>How many more bookings this slot can still take (0 when full).</summary>
    public int RemainingCapacity { get; init; }
}

/// <summary>Result of an availability lookup for one service type on one calendar day.</summary>
public record AppointmentAvailabilityDto
{
    public Guid BranchId { get; init; }
    public Guid ServiceTypeId { get; init; }
    /// <summary>The requested day, in the branch's own local calendar (yyyy-MM-dd).</summary>
    public string Date { get; init; } = string.Empty;
    /// <summary>IANA id from Branch.Timezone, echoed so the caller can label the times it renders.</summary>
    public string Timezone { get; init; } = "UTC";
    /// <summary>False when the branch's operating hours say it is shut that weekday — Slots is then empty.</summary>
    public bool IsOpen { get; init; }
    public int SlotDurationMinutes { get; init; }
    public List<AppointmentSlotDto> Slots { get; init; } = new();
}

/// <summary>
/// Staff/integration create. Field names deliberately mirror WebhooksController's inbound
/// <c>appointment.created</c> <c>data</c> object (serviceTypeId/serviceTypeCode, customerName,
/// customerPhone, customerEmail) so a partner already sending that payload can post it here
/// unchanged.
/// </summary>
public record CreateAppointmentRequest
{
    public Guid? ServiceTypeId { get; init; }
    public string? ServiceTypeCode { get; init; }

    public string CustomerName { get; init; } = string.Empty;
    public string? CustomerPhone { get; init; }
    public string? CustomerEmail { get; init; }

    /// <summary>UTC. A Local/Unspecified value is coerced to UTC server-side.</summary>
    public DateTime ScheduledAt { get; init; }
    /// <summary>Omit to use the service type's AverageServiceTimeMinutes.</summary>
    public int? DurationMinutes { get; init; }

    public string? Notes { get; init; }
    public string? ExternalReference { get; init; }
    public string? ExternalSystem { get; init; }
}

public record RescheduleAppointmentRequest
{
    public DateTime ScheduledAt { get; init; }
    public int? DurationMinutes { get; init; }
    public string? Reason { get; init; }
}

public record CancelAppointmentRequest
{
    public string? Reason { get; init; }
}

/// <summary>Anonymous booking from the public /book/{branchId} page. Kept narrower than the staff
/// create on purpose: no external references, no notes-free-for-all, no duration override.</summary>
public record PublicBookAppointmentRequest
{
    public Guid ServiceTypeId { get; init; }
    public DateTime ScheduledAt { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerPhone { get; init; } = string.Empty;
    public string? CustomerEmail { get; init; }
    public string? Notes { get; init; }
}

/// <summary>What a member of the public gets back after booking — enough to show and quote, nothing else.</summary>
public record PublicAppointmentConfirmationDto
{
    public string ReferenceCode { get; init; } = string.Empty;
    public DateTime ScheduledAt { get; init; }
    public int DurationMinutes { get; init; }
    public string ServiceTypeName { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string Timezone { get; init; } = "UTC";
}

/// <summary>The bookable services offered on the public page. Deliberately a much smaller shape
/// than <see cref="ServiceTypeDto"/> — a public page has no business knowing prefixes or codes.</summary>
public record BookableServiceTypeDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int DurationMinutes { get; init; }
    public string? Color { get; init; }
}

/// <summary>
/// Scheduling configuration, persisted inside the existing <c>Branch.Settings</c> JSON blob under
/// the key <c>"Appointments"</c> — the same read/merge/write pattern VisitorsController already
/// uses for <c>"VisitorConsent"</c> and <c>"VisitingDay"</c>. No new table and no new columns on
/// Branch, per the project's "enhance before you add" rule.
/// <para>
/// The opening hours themselves are NOT here: <c>Branch.OperatingHours</c> already exists and is
/// already populated at provisioning time, so it stays the single source of truth for when a
/// branch is open.
/// </para>
/// </summary>
public record AppointmentSettingsDto
{
    /// <summary>Minutes between successive slot starts. Null = use the service type's own duration
    /// (back-to-back slots).</summary>
    public int? SlotIntervalMinutes { get; init; }

    /// <summary>How many appointments may share one slot for the same service type (parallel
    /// counters/staff). Default 1.</summary>
    public int CapacityPerSlot { get; init; } = 1;

    /// <summary>A slot closes to new bookings this many minutes before it starts. Default 30.</summary>
    public int MinimumLeadTimeMinutes { get; init; } = 30;

    /// <summary>How far ahead bookings are accepted. Default 60 days.</summary>
    public int MaxAdvanceDays { get; init; } = 60;

    /// <summary>Whether the anonymous /book/{branchId} page may take bookings. Default true.</summary>
    public bool PublicBookingEnabled { get; init; } = true;

    /// <summary>Reminders go out this many minutes before ScheduledAt. Default 1440 (24h).</summary>
    public int ReminderLeadMinutes { get; init; } = 1440;

    /// <summary>Grace period after ScheduledAt before the sweep marks an unattended appointment
    /// NoShow. Default 30 minutes.</summary>
    public int NoShowGraceMinutes { get; init; } = 30;
}
