using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// A single visit, with the visiting person's profile fields flattened in for convenience —
// FullName/Phone/Email/IdNumber/Company/PhotoUrl/IsWatchlisted/WatchlistReason all actually
// live on VisitorProfile and are joined in at read time (see VisitorsController.MapToDto).
public record VisitorDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public Guid VisitorProfileId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Company { get; init; }
    public string? IdNumber { get; init; }
    public string? PhotoUrl { get; init; }
    public bool IsWatchlisted { get; init; }
    public string? WatchlistReason { get; init; }

    public string Purpose { get; init; } = string.Empty;
    public string? VehiclePlate { get; init; }
    public Guid? HostUserId { get; init; }
    public string HostName { get; init; } = string.Empty;
    public Guid? StudentId { get; init; }
    public string? StudentName { get; init; }

    public VisitorStatus Status { get; init; }
    public VisitorType VisitorType { get; init; }

    public DateTime? ScheduledAt { get; init; }

    // Set on a pre-registered EXPECTED arrival (Status = Expected) — when they're due.
    public DateTime? ExpectedArrivalAt { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? CheckedOutAt { get; init; }
    public DateTime CreatedAt { get; init; }

    public string? Notes { get; init; }

    // --- Contractor site induction (lives on the person's profile, flattened in like the
    // identity fields above). InductionValid is server-computed against the same validity window
    // the check-in warning uses, so no client has to know or duplicate that rule.
    public DateTime? InductionCompletedAt { get; init; }
    public DateTime? InductionExpiresAt { get; init; }
    public bool InductionValid { get; init; }
    public string? InductionNotes { get; init; }

    // When the watchlist flag was added, for the staff-facing profile panel. The REASON is
    // already on WatchlistReason above — neither is ever rendered on a kiosk/public surface.
    public DateTime? WatchlistAddedAt { get; init; }

    // Populated only on a check-in response, and only when something needed saying but wasn't
    // serious enough to refuse the visit — currently a contractor with a missing or lapsed site
    // induction. A blocking condition (watchlist) is never reported here; it's a 409 instead.
    public string? CheckInWarning { get; init; }

    // Frequency signal for staff, not a hard limit — an unusually high count here (badge
    // sharing, tailgating) is something to notice, not something the system blocks on its own.
    public int VisitsLast24Hours { get; init; }
    public int TotalVisits { get; init; }

    public DateTime? ConsentGivenAt { get; init; }

    // Only populated in the response right after a check-in/check-in-existing call — the raw
    // string to render as a QR code client-side. Never returned on ordinary reads.
    public string? BadgeQrToken { get; init; }

    // Set the first time this visit's badge QR is successfully scanned (checkout) — independent
    // of Status, so a photographed/shared code stays permanently dead even if Status is ever
    // touched by something else. See VisitorPassesController.ScanVisitBadge.
    public DateTime? BadgeConsumedAt { get; init; }
}

// Pre-register a visitor ahead of their arrival (Status starts as PreRegistered).
// Mutable (not init-only) — bound directly as a Blazor form model via @bind in
// VisitorManagement.razor, which requires settable properties.
public record PreRegisterVisitorRequest
{
    // Set when staff picked a returning-visitor match from search — skips profile
    // matching/creation entirely and links straight to this profile.
    public Guid? VisitorProfileId { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? IdNumber { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? VehiclePlate { get; set; }
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;

    // Set when checking in via the visiting-day roster search — the visitor is visiting this
    // student specifically, distinct from HostUserId/HostName which point at a staff user.
    public Guid? StudentId { get; set; }

    public DateTime? ScheduledAt { get; set; }
    public string? Notes { get; set; }

    public VisitorType VisitorType { get; set; } = VisitorType.Guest;
}

// Walk-in check-in creates and checks in a visitor in one step; checking in an existing
// pre-registered visitor is a separate action (CheckInVisitorRequest reused, optional there).
// Mutable for the same @bind reason as PreRegisterVisitorRequest above.
public record CheckInVisitorRequest
{
    public Guid? VisitorProfileId { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? IdNumber { get; set; }
    public string? PhotoUrl { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? VehiclePlate { get; set; }
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;

    // Set when checking in via the visiting-day roster search — see PreRegisterVisitorRequest.StudentId.
    public Guid? StudentId { get; set; }

    public string? Notes { get; set; }
    public bool ConsentGiven { get; set; }

    // Nullable so CheckInExisting can tell "the caller didn't say" (keep whatever the
    // pre-registered record already carries) from "the caller said Guest".
    public VisitorType? VisitorType { get; set; }

    // Contractor induction captured at the desk — written through to the person's profile when
    // supplied, so the next site they visit already knows. Null means "don't touch what's there".
    public DateTime? InductionCompletedAt { get; set; }
    public string? InductionNotes { get; set; }

    // Manager-or-above escape hatch for a watchlisted visitor. Check-in is refused outright for
    // anyone on the watchlist; supplying a reason here lets a Manager/Admin (and ONLY them —
    // Staff supplying one changes nothing) admit them anyway, with the reason written into the
    // visit's Notes so the override is on the record rather than in someone's memory.
    public string? WatchlistOverrideReason { get; set; }

    // The visiting-day repeat-check-in gate's self-service path for front-desk Staff: supplying a
    // reason both satisfies the gate AND flags the guardian's card, in one atomic server-side
    // action. It used to be two client calls (flag, then check in) — which cannot work now that a
    // watchlisted profile is blocked from checking in, since the first call would bar the second.
    public string? CardFlagReason { get; set; }
}

// Stored inside Branch.Settings under the "VisitingDay" key, alongside VisitorConsent and
// whatever else that JSON blob carries. Governs the visiting-day-specific abuse controls added
// on top of plain visitor check-in: how many times a guardian's roster card can be checked in
// before front-desk staff must flag it or a Manager+ user must complete it instead, and whether
// guardians get an SMS confirming their card was used. Mutable for the same @bind reason as the
// other settings DTOs.
public record VisitingDaySettingsDto
{
    public int CardCheckInWarningThreshold { get; set; } = 2;
    public bool NotifyGuardianOnCheckIn { get; set; }
}

public record UpdateVisitorRequest
{
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Company { get; init; }
    public string? IdNumber { get; init; }
    public string Purpose { get; init; } = string.Empty;
    public string? VehiclePlate { get; init; }
    public Guid? HostUserId { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string? Notes { get; init; }
}

public record SetWatchlistRequest
{
    public bool IsWatchlisted { get; init; }
    public string? Reason { get; init; }
}

public record DeleteVisitorRequest
{
    public string Reason { get; init; } = string.Empty;
}

public record VisitorSummaryDto
{
    public int CurrentlyOnSite { get; init; }
    public int TotalToday { get; init; }

    // Counts BOTH pre-registration statuses (PreRegistered and Expected) — reception cares that
    // someone is due, not which of the two code paths booked them.
    public int PreRegisteredUpcoming { get; init; }
    public int WatchlistedOnSite { get; init; }

    // Expected arrivals still outstanding today (not yet checked in or cancelled).
    public int ExpectedToday { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Evacuation roll-call — "who is inside the building right now", the first artefact a fire drill
// or a safeguarding audit asks for. Point-in-time by construction: nothing here is stored, it is
// recomputed on every request from the same check-in data the visitor log already keeps.
// ---------------------------------------------------------------------------------------------

public record EvacuationReportDto
{
    public DateTime GeneratedAt { get; init; }
    public string BranchName { get; init; } = string.Empty;

    // CheckedInVisitorCount + GroupPassOccupantCount. The number that goes on the clipboard.
    public int TotalOnSite { get; init; }
    public int CheckedInVisitorCount { get; init; }
    public int GroupPassOccupantCount { get; init; }

    // Whether roster students are represented in these numbers. Currently always false — see
    // StudentsNote; kept as an explicit field so a roll-call sheet can say so out loud rather
    // than leaving a marshal to assume the headcount already covers the school roll.
    public bool StudentsIncluded { get; init; }
    public string? StudentsNote { get; init; }

    public List<EvacuationPersonDto> People { get; init; } = new();
    public List<EvacuationGroupPassDto> GroupPasses { get; init; } = new();
}

public record EvacuationPersonDto
{
    public Guid VisitorId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? Company { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public VisitorType VisitorType { get; init; }
    public string? StudentName { get; init; }
}

// A group pass admits a crew under ONE badge and only tracks a headcount — individual members
// are never named anywhere, so a roll call can report the number under each pass and who to ask,
// but cannot list them. Marshals need to know that explicitly, not discover it in the car park.
public record EvacuationGroupPassDto
{
    public Guid PassId { get; init; }
    public string Label { get; init; } = string.Empty;
    public int OccupantCount { get; init; }
    public DateTime ExpiresAt { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Pre-registration of expected visitors
// ---------------------------------------------------------------------------------------------

// Mutable for the same @bind reason as the other request records on this page.
public record ExpectedVisitorEntry
{
    public Guid? VisitorProfileId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string? IdNumber { get; set; }
    public string? VehiclePlate { get; set; }
}

// One booking covering one or many people arriving together for the same reason at the same time
// — the realistic shape (an interview panel, a contractor crew, a governors' meeting), rather
// than making reception repeat the host/purpose/time for every name.
public record CreateExpectedVisitorsRequest
{
    public DateTime ExpectedArrivalAt { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public VisitorType VisitorType { get; set; } = VisitorType.Guest;
    public string? Notes { get; set; }
    public List<ExpectedVisitorEntry> Visitors { get; set; } = new();
}

// Records that a person completed a site induction. Null CompletedAt clears it (induction
// withdrawn / entered in error), which makes their next contractor check-in warn again.
public record SetInductionRequest
{
    public DateTime? CompletedAt { get; set; }
    public string? Notes { get; set; }
}

// A match returned by the returning-visitor search — used both for the staff-facing typeahead
// (prefill on selection) and, server-side, for duplicate/active-visit detection.
public record VisitorProfileSearchResultDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Company { get; init; }
    public string? IdNumber { get; init; }
    public string? PhotoUrl { get; init; }
    public bool IsWatchlisted { get; init; }
    public string? WatchlistReason { get; init; }
    public DateTime? LastVisitAt { get; init; }
    public int TotalVisits { get; init; }
    public int VisitsLast24Hours { get; init; }
    public bool HasActiveVisit { get; init; }
}

// Stored inside Branch.Settings under the "VisitorConsent" key, alongside whatever other
// unrelated settings that JSON blob already carries for this branch. Mutable (not init-only) —
// bound directly as a Blazor form model via @bind in VisitorManagement.razor's settings modal.
public record VisitorConsentSettingsDto
{
    public bool Required { get; set; }
    public string Text { get; set; } = "I consent to my visit details being recorded for security and site-access purposes.";
}

// Stored inside Organization.Settings under the "VisitorRetention" key. Org-wide (not per
// branch) since data retention is a legal/compliance policy set once for the whole company.
// Default of 730 days (2 years) is a common baseline for visitor logs kept for security/
// incident-investigation purposes — adjust to match your jurisdiction's actual requirement.
// Mutable (not init-only) — bound directly as a Blazor form model via @bind.
public record VisitorRetentionSettingsDto
{
    public int RetentionDays { get; set; } = 730;
}

public record DeletedVisitorDto
{
    public Guid Id { get; init; }
    public string BadgeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public VisitorStatus StatusAtDeletion { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime DeletedAt { get; init; }
    public string? DeletedByUserName { get; init; }
    public string DeletionReason { get; init; } = string.Empty;
}

public record VisitorPassDto
{
    public Guid Id { get; init; }
    public Guid BranchId { get; init; }
    public string Label { get; init; } = string.Empty;
    public int MaxVisitors { get; init; }
    public int CurrentVisitors { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public DateTime CreatedAt { get; init; }

    // Only populated right after creation — the raw token string to render as a QR code.
    public string? QrToken { get; init; }
}

// Mutable (not init-only) — bound directly as a Blazor form model via @bind in
// VisitorManagement.razor's Group Passes modal, same reason as PreRegisterVisitorRequest above.
public record CreateVisitorPassRequest
{
    public string Label { get; set; } = string.Empty;
    public int MaxVisitors { get; set; } = 1;
    public int ValidHours { get; set; } = 12;
}

// An individual visit badge always toggles unambiguously (it's 1:1 with one person's current
// status — check-in already happened when the badge was issued, so a scan can only mean
// checkout). A shared group pass can't disambiguate arrival vs. departure from the QR alone —
// the scanning terminal/operator supplies Direction only in that case (like separate in/out
// readers at a turnstile); it's ignored for an individual visit badge.
public record VisitorScanRequest
{
    public string Token { get; init; } = string.Empty;
    public string? Direction { get; init; } // "in" | "out" — required only for a pass token
}

public enum VisitorScanAction { CheckedIn, CheckedOut }

public enum VisitorActivityKind { CheckedIn, CheckedOut, PreRegistered, Flagged, Unflagged, Deleted }

public record VisitorActivityEvent
{
    public VisitorActivityKind Kind { get; init; }
    public VisitorDto Visitor { get; init; } = null!;
    public DateTime OccurredAt { get; init; }
}

public record VisitorScanResultDto
{
    public VisitorScanAction Action { get; init; }
    public string Message { get; init; } = string.Empty;
    public VisitorDto? Visitor { get; init; }
    public bool IsWatchlisted { get; init; }

    // Populated only when the scanned badge was a group VisitorPass, not an individual visit.
    public VisitorPassDto? Pass { get; init; }
}

// A day/count or hour/count pair for the trend and peak-hours charts on the Visitor report.
public record DayCountDto
{
    public DateOnly Day { get; init; }
    public int Count { get; init; }
}

public record HourCountDto
{
    public int Hour { get; init; }
    public int Count { get; init; }
}

public record HostVisitCountDto
{
    public string HostName { get; init; } = string.Empty;
    public int Count { get; init; }
}

// A visitor with an unusually high visit count within the reported range — the same signal as
// VisitsLast24Hours/CheckInsToday, aggregated over the report's date range instead of a rolling
// window, for a supervisor reviewing a whole day/week/month at once rather than one live search.
public record FrequentVisitorDto
{
    public Guid VisitorProfileId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public bool IsWatchlisted { get; init; }
    public int VisitCount { get; init; }
}

public record VisitorReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    public int TotalVisits { get; init; }
    public int UniqueVisitors { get; init; }
    public int WatchlistIncidents { get; init; }
    public int RosterCheckIns { get; init; }
    public double AvgDwellMinutes { get; init; }
    public double ConsentCompliancePercent { get; init; }

    public List<DayCountDto> VisitsByDay { get; init; } = new();
    public List<HourCountDto> VisitsByHour { get; init; } = new();
    public List<HostVisitCountDto> TopHosts { get; init; } = new();

    // Visitors with 3+ visits within the reported range — the same "worth a second look"
    // threshold philosophy as CardCheckInWarningThreshold, just over a longer window.
    public List<FrequentVisitorDto> FrequentVisitors { get; init; } = new();
}
