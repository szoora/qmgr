using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// ---------------------------------------------------------------------------------------------
// Visitor reporting — filters, analytics, exceptions, compliance and scheduled delivery.
//
// Lives beside VisitorDto.cs rather than inside it purely for size: VisitorDto.cs is already the
// module's operational contract (check-in, badges, evacuation) and this is its reporting contract.
// Same assembly, same namespace, same single-source-of-truth rule — both projects reference it.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Every filter the visitor report, the log export and the exception views share. One record so a
/// figure on screen and the row in the download it produced can never disagree about what was
/// asked for — the two used to take different parameters, which is how an export silently wider
/// than the summary above it happens.
///
/// All fields optional; an empty filter means "everything in range".
/// </summary>
public record VisitorReportFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public VisitorType? VisitorType { get; set; }
    public VisitorStatus? Status { get; set; }
    public string? HostName { get; set; }
    public string? Company { get; set; }
    public bool WatchlistOnly { get; set; }
    /// <summary>Roster (visiting-day) check-ins only — visits linked to a student.</summary>
    public bool RosterOnly { get; set; }
    /// <summary>Visits that came in OR left by this gate (plan §10), matched by the gate's name.</summary>
    public string? Gate { get; set; }

    /// <summary>Renders the active filters as a human sentence for a print header or an email subject.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (VisitorType.HasValue) parts.Add($"type {VisitorType}");
        if (Status.HasValue) parts.Add($"status {Status}");
        if (!string.IsNullOrWhiteSpace(HostName)) parts.Add($"host “{HostName}”");
        if (!string.IsNullOrWhiteSpace(Company)) parts.Add($"company “{Company}”");
        if (WatchlistOnly) parts.Add("watchlisted only");
        if (RosterOnly) parts.Add("visiting-day only");
        if (!string.IsNullOrWhiteSpace(Gate)) parts.Add($"gate “{Gate}”");
        return parts.Count == 0 ? "No filters" : string.Join(" · ", parts);
    }
}

// ---------------------------------------------------------------------------------------------
// Analytics
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One cell of the day-of-week × hour heatmap. Replaces the flat 24-bin hourly chart, which
/// averaged a school's Saturday visiting-day rush into the same bar as a Tuesday morning delivery
/// and so hid the single most useful pattern in the data.
///
/// DayOfWeek is 0=Sunday..6=Saturday and Hour is 0..23, both in the BRANCH'S local time — see
/// VisitorReportDto.TimeZoneId for why that matters.
/// </summary>
public record DayHourCountDto
{
    public int DayOfWeek { get; init; }
    public int Hour { get; init; }
    public int Count { get; init; }
}

public record LabelCountDto
{
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>
/// Dwell time as a distribution rather than a single mean. One four-hour contractor visit drags an
/// average far enough to make it meaningless on a day of twenty-minute parent visits, so the
/// average is kept but no longer stands alone.
/// </summary>
public record DwellBucketDto
{
    public string Label { get; init; } = string.Empty;
    public int MinMinutes { get; init; }
    public int? MaxMinutes { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// What happened to visits that were booked in advance. The no-show rate is the figure that
/// justifies pre-registration to whoever pays for it, and nothing reported it before.
/// </summary>
public record PreRegistrationOutcomeDto
{
    public int Booked { get; init; }
    public int Arrived { get; init; }
    public int NoShow { get; init; }
    public int Cancelled { get; init; }
    public double ArrivalRatePercent { get; init; }
    public double NoShowRatePercent { get; init; }
}

public record NewVsReturningDto
{
    /// <summary>Profiles whose first ever visit falls inside the reported range.</summary>
    public int NewVisitors { get; init; }
    /// <summary>Profiles that had already visited before the range started.</summary>
    public int ReturningVisitors { get; init; }
    public double ReturningPercent { get; init; }
}

// ---------------------------------------------------------------------------------------------
// The main report
// ---------------------------------------------------------------------------------------------

/// <summary>
/// The visitor report over a date range. Extends the original summary rather than replacing it —
/// every field the first version returned is still here and still means the same thing.
/// </summary>
public record VisitorReportDtoV2
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    /// <summary>
    /// The IANA zone the day and hour buckets below were computed in — the branch's own
    /// Branch.Timezone, not UTC. Every timestamp in this system is stored UTC, so bucketing on the
    /// raw value put a 21:00 UTC check-in at midnight in Kampala and therefore on the WRONG DAY.
    /// Reported explicitly so a printed sheet can say which clock it is quoting.
    /// </summary>
    public string TimeZoneId { get; init; } = "UTC";
    public string ScopeName { get; init; } = string.Empty;
    public string FilterDescription { get; init; } = string.Empty;

    public int TotalVisits { get; init; }
    public int UniqueVisitors { get; init; }
    public int WatchlistIncidents { get; init; }
    public int RosterCheckIns { get; init; }
    public double AvgDwellMinutes { get; init; }
    public double ConsentCompliancePercent { get; init; }

    /// <summary>
    /// How many visits the consent percentage was computed over. Without it a "100%" from two
    /// visits reads identically to a "100%" from two thousand, and a report that quietly drops
    /// rows with no consent record would overstate compliance to whoever signs it off.
    /// </summary>
    public int ConsentDenominator { get; init; }
    /// <summary>Completed visits that have both a check-in and a check-out, so dwell is real.</summary>
    public int DwellDenominator { get; init; }

    public List<DayCountDto> VisitsByDay { get; init; } = new();
    public List<HourCountDto> VisitsByHour { get; init; } = new();
    public List<DayHourCountDto> VisitsByDayHour { get; init; } = new();
    public List<HostVisitCountDto> TopHosts { get; init; } = new();
    public List<LabelCountDto> ByVisitorType { get; init; } = new();
    public List<LabelCountDto> ByCompany { get; init; } = new();
    public List<DwellBucketDto> DwellDistribution { get; init; } = new();
    public List<FrequentVisitorDto> FrequentVisitors { get; init; } = new();

    public PreRegistrationOutcomeDto PreRegistration { get; init; } = new();
    public NewVsReturningDto Audience { get; init; } = new();

    /// <summary>Per-branch comparison. Empty for a single-branch report; populated at org scope.</summary>
    public List<BranchVisitSummaryDto> Branches { get; init; } = new();

    /// <summary>
    /// Visitors by gate (plan §10): entries, exits, who is on site now by the gate they came in, and arrivals by
    /// local hour per gate — the visiting-day staffing question. Visits with no gate recorded are one row named
    /// <see cref="NoGateLabel"/> so the totals still add up. Empty when no visit in range carries a gate.
    /// </summary>
    public List<VisitorGateCountDto> Gates { get; init; } = new();

    /// <summary>The row name for visits that recorded no gate (older visits, or a branch with none).</summary>
    public const string NoGateLabel = "Not recorded";
}

public record BranchVisitSummaryDto
{
    public Guid BranchId { get; init; }
    public string BranchName { get; init; } = string.Empty;
    public int TotalVisits { get; init; }
    public int UniqueVisitors { get; init; }
    public int StillOnSite { get; init; }
    public double AvgDwellMinutes { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Exceptions — the rows that need somebody to do something
// ---------------------------------------------------------------------------------------------

/// <summary>Why a visit landed on the exception list. One row can only carry one reason.</summary>
public enum VisitorExceptionKind
{
    /// <summary>Checked in on an earlier local day and never checked out.</summary>
    StillOnSite = 0,
    /// <summary>On site longer than the branch's overstay threshold, but since today.</summary>
    Overstayed = 1,
    /// <summary>A completed visit with no host recorded — a safeguarding gap, not a typo.</summary>
    NoHostRecorded = 2,
    /// <summary>A contractor admitted with no site induction, or one that had already lapsed.</summary>
    InductionLapsed = 3,
    /// <summary>Unusually many visits inside the reported range.</summary>
    FrequentVisitor = 4
}

public record VisitorExceptionDto
{
    public VisitorExceptionKind Kind { get; init; }
    public Guid VisitorId { get; init; }
    public Guid VisitorProfileId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Company { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public VisitorType VisitorType { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? CheckedOutAt { get; init; }
    public bool IsWatchlisted { get; init; }
    /// <summary>Hours on site so far, for the two open-visit kinds. Null where it does not apply.</summary>
    public double? HoursOnSite { get; init; }
    /// <summary>Plain-English statement of what is wrong with this row, ready to print.</summary>
    public string Detail { get; init; } = string.Empty;
}

public record VisitorExceptionsDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    public string ScopeName { get; init; } = string.Empty;
    public int OverstayThresholdHours { get; init; }
    public int FrequentVisitorThreshold { get; init; }

    public List<VisitorExceptionDto> StillOnSite { get; init; } = new();
    public List<VisitorExceptionDto> Overstayed { get; init; } = new();
    public List<VisitorExceptionDto> NoHostRecorded { get; init; } = new();
    public List<VisitorExceptionDto> InductionLapsed { get; init; } = new();
    public List<VisitorExceptionDto> FrequentVisitors { get; init; } = new();

    public int TotalCount => StillOnSite.Count + Overstayed.Count + NoHostRecorded.Count
                             + InductionLapsed.Count + FrequentVisitors.Count;

    public IEnumerable<VisitorExceptionDto> All() =>
        StillOnSite.Concat(Overstayed).Concat(NoHostRecorded).Concat(InductionLapsed).Concat(FrequentVisitors);
}

// ---------------------------------------------------------------------------------------------
// Compliance
// ---------------------------------------------------------------------------------------------

public record InductionRecordDto
{
    public Guid VisitorId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? Company { get; init; }
    public DateTime? CheckedInAt { get; init; }
    public DateTime? InductionCompletedAt { get; init; }
    public DateTime? InductionExpiresAt { get; init; }
    /// <summary>Whether the induction was valid ON THE DAY OF THIS VISIT, not as of now.</summary>
    public bool ValidOnVisitDate { get; init; }
    public string? InductionNotes { get; init; }
}

public record WatchlistActivityDto
{
    public Guid VisitorId { get; init; }
    public Guid VisitorProfileId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public DateTime? OccurredAt { get; init; }
    /// <summary>"Admitted on override" or "Flagged" — what actually happened to this person.</summary>
    public string Event { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string? OverrideReason { get; init; }
}

public record ConsentGapDto
{
    public Guid VisitorId { get; init; }
    public string BadgeCode { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public DateTime? CheckedInAt { get; init; }
    public string Purpose { get; init; } = string.Empty;
}

/// <summary>
/// Evidence the retention policy actually ran. Recorded by VisitorRetentionJob into
/// Organization.Settings — before this, the job wrote a line to the application log and nothing
/// queryable, so an organization could not demonstrate its own stated policy had been enforced.
/// </summary>
public record RetentionEvidenceDto
{
    public int RetentionDays { get; init; }
    public DateTime? LastRunAt { get; init; }
    public int LastRunVisitsPurged { get; init; }
    public int LastRunProfilesPurged { get; init; }
    public int TotalVisitsPurged { get; init; }
    public int TotalProfilesPurged { get; init; }
    /// <summary>Oldest visit still held. Should always be inside the retention window.</summary>
    public DateTime? OldestRetainedVisitAt { get; init; }
}

public record VisitorComplianceDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string ScopeName { get; init; } = string.Empty;

    public int ContractorVisits { get; init; }
    public int ContractorVisitsWithValidInduction { get; init; }
    public double InductionCompliancePercent { get; init; }

    /// <summary>
    /// Whether this branch requires consent at all. Without it a branch that has the requirement
    /// switched off reports "0% consent captured", which reads as a compliance failure when in
    /// fact there is nothing to comply with — the difference between a breach and a non-applicable
    /// control, and exactly the distinction an auditor cares about.
    /// </summary>
    public bool ConsentRequired { get; init; }
    public int ConsentRequiredVisits { get; init; }
    public int ConsentCapturedVisits { get; init; }
    public double ConsentCompliancePercent { get; init; }

    public List<InductionRecordDto> InductionRegister { get; init; } = new();
    public List<WatchlistActivityDto> WatchlistActivity { get; init; } = new();
    public List<ConsentGapDto> ConsentGaps { get; init; } = new();
    public RetentionEvidenceDto Retention { get; init; } = new();
}

// ---------------------------------------------------------------------------------------------
// Settings: overstay thresholds and scheduled delivery
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Stored in Branch.Settings under "VisitorReporting", alongside the consent and visiting-day
/// settings already there — a new nullable-ish JSON key on a table that exists, rather than a new
/// table, per the project's enhance-before-you-add convention.
///
/// Mutable for the same @bind reason as the other settings records.
/// </summary>
public record VisitorReportingSettingsDto
{
    /// <summary>Hours on site before a still-open visit counts as an overstay. Default is a working day.</summary>
    public int OverstayThresholdHours { get; set; } = 8;

    /// <summary>
    /// Contractors legitimately stay longer than a parent on a twenty-minute visit, so they get
    /// their own threshold. Null means "use OverstayThresholdHours for them too".
    /// </summary>
    public int? ContractorOverstayThresholdHours { get; set; } = 12;

    /// <summary>Visits inside the reported range before a visitor is listed as unusually frequent.</summary>
    public int FrequentVisitorThreshold { get; set; } = 3;

    /// <summary>Where the evacuation roll call is emailed. Blank disables the send button.</summary>
    public string? EvacuationEmail { get; set; }

    public List<ReportSubscriptionDto> Subscriptions { get; set; } = new();
}

public enum ReportSubscriptionCadence
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2
}

public enum ReportSubscriptionKind
{
    /// <summary>The range summary — headline figures, trends and rankings.</summary>
    Summary = 0,
    /// <summary>The row-per-visit register as a CSV attachment.</summary>
    VisitorLog = 1,
    /// <summary>Only the rows needing action. The one worth having at 18:00 every day.</summary>
    Exceptions = 2,
    /// <summary>Induction, watchlist, consent and retention evidence.</summary>
    Compliance = 3
}

/// <summary>
/// One scheduled delivery. Held as a JSON array inside VisitorReportingSettingsDto rather than in
/// its own table: it is a short list, nothing joins to it, and it has no lifecycle independent of
/// the branch it belongs to. If per-recipient delivery history is ever wanted, THAT is the point
/// at which a real table earns its place — not before.
/// </summary>
public record ReportSubscriptionDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public ReportSubscriptionKind Kind { get; set; } = ReportSubscriptionKind.Exceptions;
    public ReportSubscriptionCadence Cadence { get; set; } = ReportSubscriptionCadence.Daily;

    /// <summary>Local hour of day (branch time) to send at. 18 = end of the working day.</summary>
    public int SendAtHour { get; set; } = 18;

    /// <summary>Comma or semicolon separated. Validated server-side before anything is sent.</summary>
    public string Recipients { get; set; } = string.Empty;

    /// <summary>Set by the job after a successful send, so a cadence fires once per period.</summary>
    public DateTime? LastSentAt { get; set; }

    public IEnumerable<string> RecipientList() =>
        (Recipients ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

public record EmailEvacuationRequest
{
    /// <summary>Overrides the branch's configured address for a one-off send. Optional.</summary>
    public string? To { get; set; }
}
