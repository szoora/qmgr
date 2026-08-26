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

    public DateTime? ScheduledAt { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? CheckedOutAt { get; init; }
    public DateTime CreatedAt { get; init; }

    public string? Notes { get; init; }

    // Frequency signal for staff, not a hard limit — an unusually high count here (badge
    // sharing, tailgating) is something to notice, not something the system blocks on its own.
    public int VisitsLast24Hours { get; init; }
    public int TotalVisits { get; init; }

    public DateTime? ConsentGivenAt { get; init; }

    // Only populated in the response right after a check-in/check-in-existing call — the raw
    // string to render as a QR code client-side. Never returned on ordinary reads.
    public string? BadgeQrToken { get; init; }
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
    public int PreRegisteredUpcoming { get; init; }
    public int WatchlistedOnSite { get; init; }
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
