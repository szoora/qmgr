using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Authorization;
using QMgr.Filters;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Controllers.v1;

[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[Authorize] // SECURITY: baseline safety net — every action already has its own [RequirePermission]
[RequireModule(ModuleCodes.VisitorSafeguarding)]
public class VisitorsController : ControllerBase
{
    private readonly QMgrDbContext _context;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly INotificationService _notificationService;
    private readonly IVisitorBadgeTokenService _badgeTokenService;
    private readonly IVisitorActivityBroadcaster _activityBroadcaster;
    private readonly IMediaStorageService _mediaStorage;
    private readonly ILogger<VisitorsController> _logger;

    private const int SearchResultLimit = 10;
    private const long MaxPhotoSizeBytes = 5 * 1024 * 1024; // 5MB — a headshot, not a document
    private static readonly TimeSpan BadgeValidity = TimeSpan.FromHours(16); // covers a full working day

    /// <summary>
    /// How long a recorded contractor site induction stays valid before check-in starts warning
    /// again. A deliberate constant rather than a per-branch setting: this feature is a flag and a
    /// date, not an induction-management module, and one more knob in Visitor Settings buys very
    /// little for a rule ("re-induct annually") that is near-universal. If a site genuinely needs a
    /// different window, promote this to VisitingDaySettingsDto — it's a settled-with-the-user
    /// decision, not a code change to make on a hunch.
    /// </summary>
    private const int InductionValidityDays = 365;

    /// <summary>Cap on one pre-registration batch — a coach party, not a bulk roster import.</summary>
    private const int MaxExpectedBatchSize = 50;

    public VisitorsController(
        QMgrDbContext context,
        ITenantContextAccessor tenantAccessor,
        INotificationService notificationService,
        IVisitorBadgeTokenService badgeTokenService,
        IVisitorActivityBroadcaster activityBroadcaster,
        IMediaStorageService mediaStorage,
        ILogger<VisitorsController> logger)
    {
        _context = context;
        _tenantAccessor = tenantAccessor;
        _notificationService = notificationService;
        _badgeTokenService = badgeTokenService;
        _activityBroadcaster = activityBroadcaster;
        _mediaStorage = mediaStorage;
        _logger = logger;
    }

    /// <summary>
    /// Visitor (like Counter/Token/Feedback) has no global EF query filter — it's branch-scoped,
    /// not directly org-scoped — so every action reaching one by branchId must verify ownership
    /// explicitly. Visitor rows carry real PII (name, phone, email, ID number), so this is a
    /// genuine data-exposure boundary. SuperAdmin bypass matches every other VerifyBranchOwnership
    /// in this codebase.
    /// </summary>
    private async Task<IActionResult?> VerifyBranchOwnership(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved)
            return Unauthorized(new ProblemDetails
            {
                Title = "Tenant not resolved",
                Detail = "Unable to determine your organization context.",
                Status = StatusCodes.Status401Unauthorized
            });

        if (RoleCodes.IsSuperAdmin(tenantContext.UserRole))
        {
            var superAdminBranchExists = await _context.Branches.AnyAsync(b => b.Id == branchId);
            return superAdminBranchExists ? null : NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' does not exist.",
                Status = StatusCodes.Status404NotFound
            });
        }

        var branchExists = await _context.Branches
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == tenantContext.OrganizationId);

        if (!branchExists)
            return NotFound(new ProblemDetails
            {
                Title = "Branch not found",
                Detail = $"Branch with ID '{branchId}' was not found in your organization.",
                Status = StatusCodes.Status404NotFound
            });

        return null;
    }

    private async Task<Guid> ResolveOrganizationIdAsync(Guid branchId)
    {
        var tenantContext = _tenantAccessor.TenantContext!;
        return RoleCodes.IsSuperAdmin(tenantContext.UserRole)
            ? (await _context.Branches.Where(b => b.Id == branchId).Select(b => b.OrganizationId).FirstAsync())
            : tenantContext.OrganizationId;
    }

    private const string ConsentSettingsKey = "VisitorConsent";

    private static VisitorConsentSettingsDto ReadConsentSettings(string? branchSettingsJson)
    {
        if (string.IsNullOrEmpty(branchSettingsJson)) return new VisitorConsentSettingsDto { Required = false };
        try
        {
            var root = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(branchSettingsJson);
            if (root != null && root.TryGetValue(ConsentSettingsKey, out var element))
                return System.Text.Json.JsonSerializer.Deserialize<VisitorConsentSettingsDto>(element.GetRawText()) ?? new VisitorConsentSettingsDto { Required = false };
        }
        catch (System.Text.Json.JsonException) { /* malformed settings blob — treat as not configured */ }
        return new VisitorConsentSettingsDto { Required = false };
    }

    private static string WriteConsentSettings(string? branchSettingsJson, VisitorConsentSettingsDto consent)
    {
        var merged = string.IsNullOrEmpty(branchSettingsJson)
            ? new Dictionary<string, object>()
            : (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(branchSettingsJson) ?? new())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        merged[ConsentSettingsKey] = consent;
        return System.Text.Json.JsonSerializer.Serialize(merged);
    }

    private const string VisitingDaySettingsKey = "VisitingDay";

    private static VisitingDaySettingsDto ReadVisitingDaySettings(string? branchSettingsJson)
    {
        if (string.IsNullOrEmpty(branchSettingsJson)) return new VisitingDaySettingsDto();
        try
        {
            var root = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(branchSettingsJson);
            if (root != null && root.TryGetValue(VisitingDaySettingsKey, out var element))
                return System.Text.Json.JsonSerializer.Deserialize<VisitingDaySettingsDto>(element.GetRawText()) ?? new VisitingDaySettingsDto();
        }
        catch (System.Text.Json.JsonException) { /* malformed settings blob — treat as not configured */ }
        return new VisitingDaySettingsDto();
    }

    private static string WriteVisitingDaySettings(string? branchSettingsJson, VisitingDaySettingsDto settings)
    {
        var merged = string.IsNullOrEmpty(branchSettingsJson)
            ? new Dictionary<string, object>()
            : (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(branchSettingsJson) ?? new())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        merged[VisitingDaySettingsKey] = settings;
        return System.Text.Json.JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// How many times this profile has already been checked in today (UTC calendar day) —
    /// same definition StudentsController.SearchStudents uses for CheckInsToday, factored out
    /// here so the visiting-day repeat check-in gate in CheckIn uses the identical count the
    /// front-desk search UI already showed staff before they picked this guardian.
    /// </summary>
    private async Task<int> GetCheckInsTodayAsync(Guid profileId)
    {
        var today = DateTime.UtcNow.Date;
        return await _context.Visitors.CountAsync(v =>
            v.VisitorProfileId == profileId && v.DeletedAt == null &&
            v.CheckedInAt != null && v.CheckedInAt.Value.Date == today);
    }

    private Guid? CurrentUserId()
    {
        var raw = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var uid) ? uid : null;
    }

    /// <summary>
    /// CONCURRENCY: mirrors TokenRepository.GetNextTokenNumberAsync — a plain read-then-increment
    /// of "today's last badge number" would let two front-desk check-ins issued at the same instant
    /// compute the same badge code. pg_advisory_xact_lock, keyed per branch+day, serializes
    /// concurrent callers for the lifetime of the enclosing transaction.
    /// </summary>
    private async Task<string> GenerateBadgeCodeAsync(Guid branchId)
    {
        var today = DateTime.UtcNow.Date;
        var lockKey = $"visitor-badge:{branchId}:{today:yyyyMMdd}";
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)");

        var countToday = await _context.Visitors
            .CountAsync(v => v.BranchId == branchId && v.CreatedAt >= today);

        return $"V-{today:yyyyMMdd}-{(countToday + 1):D4}";
    }

    /// <summary>
    /// Finds the profile matching any of the given identifiers (Email, then Phone, then
    /// IdNumber — email is the most stable identity signal a real person reuses; name alone is
    /// never used as a match key, only for display/search), or creates one. When a match is
    /// found, backfills any identifier the profile didn't have yet — the unique partial indexes
    /// turn a colliding backfill into a clean 409 via the global exception middleware rather than
    /// silently merging two different people.
    /// </summary>
    private async Task<VisitorProfile> FindOrCreateProfileAsync(
        Guid organizationId, Guid? explicitProfileId,
        string fullName, string? phone, string? email, string? idNumber, string? company, string? photoUrl)
    {
        if (explicitProfileId.HasValue)
        {
            var chosen = await _context.VisitorProfiles.FirstOrDefaultAsync(
                p => p.Id == explicitProfileId.Value && p.OrganizationId == organizationId && p.DeletedAt == null);
            if (chosen != null) return chosen;
        }

        var normEmail = VisitorMatching.NormalizeEmail(email);
        var normPhone = VisitorMatching.NormalizePhone(phone);
        var normId = VisitorMatching.NormalizeIdNumber(idNumber);

        VisitorProfile? match = null;
        if (normEmail != null)
            match = await _context.VisitorProfiles.FirstOrDefaultAsync(
                p => p.OrganizationId == organizationId && p.DeletedAt == null && p.NormalizedEmail == normEmail);
        if (match == null && normPhone != null)
            match = await _context.VisitorProfiles.FirstOrDefaultAsync(
                p => p.OrganizationId == organizationId && p.DeletedAt == null && p.NormalizedPhone == normPhone);
        if (match == null && normId != null)
            match = await _context.VisitorProfiles.FirstOrDefaultAsync(
                p => p.OrganizationId == organizationId && p.DeletedAt == null && p.NormalizedIdNumber == normId);

        if (match != null)
        {
            if (match.NormalizedEmail == null && normEmail != null) { match.Email = email; match.NormalizedEmail = normEmail; }
            if (match.NormalizedPhone == null && normPhone != null) { match.Phone = phone; match.NormalizedPhone = normPhone; }
            if (match.NormalizedIdNumber == null && normId != null) { match.IdNumber = idNumber; match.NormalizedIdNumber = normId; }
            if (!string.IsNullOrWhiteSpace(company)) match.Company = company;
            if (!string.IsNullOrWhiteSpace(photoUrl)) match.PhotoUrl = photoUrl;
            match.UpdatedAt = DateTime.UtcNow;
            return match;
        }

        var profile = new VisitorProfile
        {
            OrganizationId = organizationId,
            FullName = fullName,
            Phone = phone,
            NormalizedPhone = normPhone,
            Email = email,
            NormalizedEmail = normEmail,
            IdNumber = idNumber,
            NormalizedIdNumber = normId,
            Company = company,
            PhotoUrl = photoUrl
        };
        _context.VisitorProfiles.Add(profile);
        return profile;
    }

    /// <summary>
    /// "Duplicate" in the sense actually requested: a profile can't have two simultaneously
    /// active visits. Not branch-scoped — a person checked in at one branch can't also be
    /// checked in at another. Doesn't restrict historical visits at all.
    /// </summary>
    private async Task<Visitor?> GetActiveVisitAsync(Guid profileId, Guid? excludeVisitId = null)
    {
        var query = _context.Visitors.Where(v =>
            v.VisitorProfileId == profileId && v.DeletedAt == null && v.Status == VisitorStatus.CheckedIn);
        if (excludeVisitId.HasValue) query = query.Where(v => v.Id != excludeVisitId.Value);
        return await query.Include(v => v.Branch).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Resolves the visiting-day student link, if any — validates it belongs to this branch, and
    /// hands back sensible defaults for Purpose/HostName when the caller left them blank, since
    /// a roster-driven check-in shouldn't require staff to retype "Visiting day — see [Student]"
    /// for every single family that walks through the gate.
    /// </summary>
    private async Task<(Guid? StudentId, string? StudentName, string? DefaultHostName, string? DefaultPurpose)> ResolveStudentAsync(Guid? studentId, Guid branchId)
    {
        if (!studentId.HasValue) return (null, null, null, null);

        var student = await _context.Students.FirstOrDefaultAsync(s => s.Id == studentId.Value && s.BranchId == branchId && s.IsActive);
        if (student == null) return (null, null, null, null); // stale/invalid reference — fail open to a plain walk-in rather than 400 the whole check-in

        return (student.Id, student.FullName, student.FullName, $"Visiting day — see {student.FullName}");
    }

    internal record ProfileStats(int Total, int Last24h);

    /// <summary>
    /// Bulk visit-count lookup for a batch of profiles — one grouped query regardless of list
    /// size, not one query per row. This is the piece that keeps the list/search endpoints fast.
    /// </summary>
    private async Task<Dictionary<Guid, ProfileStats>> GetStatsAsync(IEnumerable<Guid> profileIds)
    {
        var ids = profileIds.Distinct().ToList();
        if (ids.Count == 0) return new();

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var rows = await _context.Visitors
            .Where(v => ids.Contains(v.VisitorProfileId) && v.DeletedAt == null)
            .GroupBy(v => v.VisitorProfileId)
            .Select(g => new
            {
                ProfileId = g.Key,
                Total = g.Count(),
                Last24h = g.Count(v => v.CreatedAt >= cutoff)
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.ProfileId, r => new ProfileStats(r.Total, r.Last24h));
    }

    /// <summary>
    /// Npgsql rejects a Kind=Unspecified DateTime against a "timestamp with time zone" column, and
    /// System.Text.Json hands back Unspecified for any wire value written without a trailing "Z"
    /// or offset — which a hand-rolled client or a `datetime-local` value that skipped
    /// ToUniversalTime() will be. Treating Unspecified as already-UTC (rather than calling
    /// ToUniversalTime(), which would silently shift it by the SERVER's timezone) is the only
    /// interpretation that can't corrupt the value.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static bool IsInductionValid(VisitorProfile p) =>
        p.InductionCompletedAt.HasValue && p.InductionCompletedAt.Value.AddDays(InductionValidityDays) > DateTime.UtcNow;

    /// <summary>
    /// Warn-not-block: a contractor arriving without a current site induction is a real problem
    /// worth putting in front of the person on the desk, but refusing entry over a paperwork date
    /// would strand a crew at the gate over something a supervisor can resolve in a minute. The
    /// watchlist is the only thing in this module that actually bars anyone.
    /// </summary>
    private static string? InductionWarning(Visitor v, VisitorProfile p)
    {
        if (v.VisitorType != VisitorType.Contractor) return null;
        if (!p.InductionCompletedAt.HasValue)
            return $"{p.FullName} is checking in as a contractor with no recorded site induction.";
        if (!IsInductionValid(p))
            return $"{p.FullName}'s site induction lapsed on {p.InductionCompletedAt.Value.AddDays(InductionValidityDays):MMM dd, yyyy} and needs renewing.";
        return null;
    }

    /// <summary>
    /// BLOCK, not warn. A watchlisted profile is refused check-in outright — the whole point of
    /// flagging someone is that they should not be admitted, and a dismissible banner on a busy
    /// front desk is not a control. The reason is returned in the ProblemDetails because every
    /// caller of this is permission-gated staff (VisitorsCheckIn); it is never rendered on a
    /// kiosk/public surface, which reads VisitorScanResultDto/CustomerDisplay, not this.
    ///
    /// The single way past it is a Manager-or-above supplying WatchlistOverrideReason, which is
    /// then written into the visit's Notes — an override that leaves a record, rather than a
    /// silent one. Staff supplying a reason changes nothing; they need a manager.
    /// </summary>
    private IActionResult? WatchlistBlock(VisitorProfile profile, string? overrideReason)
    {
        if (!profile.IsWatchlisted) return null;

        var isManager = RoleCodes.IsManagerOrAbove(_tenantAccessor.TenantContext!.UserRole);
        if (isManager && !string.IsNullOrWhiteSpace(overrideReason)) return null;

        var since = profile.WatchlistAddedAt.HasValue ? $" (flagged {profile.WatchlistAddedAt.Value:MMM dd, yyyy})" : "";
        var reason = string.IsNullOrWhiteSpace(profile.WatchlistReason) ? "No reason was recorded." : profile.WatchlistReason;
        return Conflict(new ProblemDetails
        {
            Title = "Visitor is on the watchlist",
            Detail = isManager
                ? $"{profile.FullName} is flagged{since}: {reason} As a Manager you may admit them anyway by supplying an override reason, which is recorded against the visit."
                : $"{profile.FullName} is flagged{since}: {reason} Do not admit them — a Manager or Admin must authorise this check-in.",
            Status = StatusCodes.Status409Conflict
        });
    }

    /// <summary>Applies a watchlist flag/unflag to a profile, keeping who/when in step with it.</summary>
    private void ApplyWatchlist(VisitorProfile profile, bool isWatchlisted, string? reason)
    {
        profile.IsWatchlisted = isWatchlisted;
        profile.WatchlistReason = isWatchlisted ? reason : null;
        profile.WatchlistAddedAt = isWatchlisted ? DateTime.UtcNow : null;
        profile.WatchlistAddedByUserId = isWatchlisted ? CurrentUserId() : null;
        profile.UpdatedAt = DateTime.UtcNow;
    }

    internal static VisitorDto MapToDto(Visitor v, VisitorProfile p, ProfileStats? stats = null, string? qrToken = null, string? checkInWarning = null) => new()
    {
        Id = v.Id,
        BranchId = v.BranchId,
        VisitorProfileId = p.Id,
        BadgeCode = v.BadgeCode,
        FullName = p.FullName,
        Phone = p.Phone,
        Email = p.Email,
        Company = p.Company,
        IdNumber = p.IdNumber,
        PhotoUrl = p.PhotoUrl,
        IsWatchlisted = p.IsWatchlisted,
        WatchlistReason = p.WatchlistReason,
        WatchlistAddedAt = p.WatchlistAddedAt,
        InductionCompletedAt = p.InductionCompletedAt,
        InductionExpiresAt = p.InductionCompletedAt?.AddDays(InductionValidityDays),
        InductionValid = IsInductionValid(p),
        InductionNotes = p.InductionNotes,
        CheckInWarning = checkInWarning,
        Purpose = v.Purpose,
        VehiclePlate = v.VehiclePlate,
        HostUserId = v.HostUserId,
        HostName = v.HostName,
        StudentId = v.StudentId,
        StudentName = v.StudentName,
        Status = v.Status,
        VisitorType = v.VisitorType,
        ScheduledAt = v.ScheduledAt,
        ExpectedArrivalAt = v.ExpectedArrivalAt,
        CheckedInAt = v.CheckedInAt,
        CheckedOutAt = v.CheckedOutAt,
        BadgeConsumedAt = v.BadgeConsumedAt,
        CreatedAt = v.CreatedAt,
        Notes = v.Notes,
        ConsentGivenAt = v.ConsentGivenAt,
        TotalVisits = stats?.Total ?? 1,
        VisitsLast24Hours = stats?.Last24h ?? 1,
        BadgeQrToken = qrToken
    };

    private async Task NotifyHostAsync(Visitor visitor, VisitorProfile profile, Guid organizationId, Guid branchId)
    {
        if (visitor.HostUserId is not { } hostUserId) return;

        try
        {
            await _notificationService.CreateInAppNotificationAsync(new CreateNotificationRequest
            {
                UserId = hostUserId,
                BranchId = branchId,
                OrganizationId = organizationId,
                Title = "Visitor arrived",
                Message = $"{profile.FullName} has checked in to see you ({visitor.Purpose}).",
                Type = NotificationType.VisitorArrived,
                Priority = NotificationPriority.High
            });
        }
        catch (Exception ex)
        {
            // Host notification failing shouldn't fail the check-in itself — the visitor is
            // still on the log and can be found by front desk staff.
            _logger.LogError(ex, "Failed to notify host {HostUserId} of visitor {VisitorId} arrival", hostUserId, visitor.Id);
        }
    }

    /// <summary>
    /// Confirms to a guardian, by SMS, that their roster card was just used to check in — opt-in
    /// per branch (VisitingDaySettingsDto.NotifyGuardianOnCheckIn) since it costs real money and
    /// isn't every school's preference. Doubles as its own abuse control: a guardian who didn't
    /// actually visit gets an immediate signal their card was used without them. Only fires for
    /// roster (StudentId-linked) check-ins — SendSmsAsync already no-ops safely (returns false,
    /// logs) when SMS isn't configured for the org, so no extra guard needed for that case.
    /// </summary>
    private async Task NotifyGuardianAsync(Visitor visitor, VisitorProfile profile, Guid organizationId, VisitingDaySettingsDto settings)
    {
        if (!settings.NotifyGuardianOnCheckIn) return;
        if (visitor.StudentId == null) return;
        if (string.IsNullOrWhiteSpace(profile.Phone)) return;

        try
        {
            var message = $"Q-Mgr: {profile.FullName}, your card was just used to check in to see {visitor.StudentName}. " +
                          "If this wasn't you, please contact the front desk immediately.";
            await _notificationService.SendSmsAsync(organizationId, profile.Phone, message);
        }
        catch (Exception ex)
        {
            // Same reasoning as NotifyHostAsync — a failed guardian SMS shouldn't fail the
            // check-in itself.
            _logger.LogError(ex, "Failed to notify guardian for visitor {VisitorId}", visitor.Id);
        }
    }

    /// <summary>
    /// Lists visitors for a branch, optionally filtered by status. Defaults to today's visitors.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(List<VisitorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVisitors(
        Guid branchId,
        [FromQuery] VisitorStatus? status = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] bool watchlistOnly = false)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var query = _context.Visitors.Include(v => v.VisitorProfile)
            .Where(v => v.BranchId == branchId && v.DeletedAt == null);

        if (status.HasValue)
            query = query.Where(v => v.Status == status.Value);

        if (fromDate.HasValue)
        {
            // Explicit lower bound (reporting/history use) — plain "created on/after" range.
            query = query.Where(v => v.CreatedAt >= fromDate.Value);
        }
        else
        {
            // Default "today" view: either logged today (walk-ins, same-day pre-registrations)
            // OR scheduled for today regardless of when the pre-registration was created —
            // otherwise a visitor pre-registered several days ahead of their visit never appears
            // to front-desk staff on the day they actually arrive.
            var today = DateTime.UtcNow.Date;
            query = query.Where(v => v.CreatedAt >= today || (v.ScheduledAt != null && v.ScheduledAt.Value.Date == today));
        }

        if (watchlistOnly)
            query = query.Where(v => v.VisitorProfile!.IsWatchlisted);

        var visits = await query.OrderByDescending(v => v.CreatedAt).ToListAsync();
        var stats = await GetStatsAsync(visits.Select(v => v.VisitorProfileId));

        return Ok(visits.Select(v => MapToDto(v, v.VisitorProfile!, stats.GetValueOrDefault(v.VisitorProfileId))).ToList());
    }

    [HttpGet("branches/{branchId:guid}/visitors/summary")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var today = DateTime.UtcNow.Date;
        var todaysVisitors = await _context.Visitors.Include(v => v.VisitorProfile)
            .Where(v => v.BranchId == branchId && v.DeletedAt == null &&
                        (v.CreatedAt >= today || (v.ScheduledAt != null && v.ScheduledAt.Value.Date == today)))
            .ToListAsync();

        return Ok(new VisitorSummaryDto
        {
            CurrentlyOnSite = todaysVisitors.Count(v => v.Status == VisitorStatus.CheckedIn),
            TotalToday = todaysVisitors.Count,
            // Both pre-registration statuses — reception cares that someone is due, not which of
            // the two code paths (older PreRegister vs. the expected-arrivals batch) booked them.
            PreRegisteredUpcoming = todaysVisitors.Count(v => v.Status == VisitorStatus.PreRegistered || v.Status == VisitorStatus.Expected),
            ExpectedToday = todaysVisitors.Count(v => v.Status == VisitorStatus.Expected),
            WatchlistedOnSite = todaysVisitors.Count(v => v.Status == VisitorStatus.CheckedIn && v.VisitorProfile!.IsWatchlisted)
        });
    }

    /// <summary>
    /// EVACUATION ROLL CALL — everyone inside this branch at this instant. The first thing a fire
    /// marshal or a safeguarding inspector asks for, assembled entirely from data check-in already
    /// captures; nothing here is stored or scheduled, it's recomputed per request so it can never
    /// be stale.
    ///
    /// Two populations, deliberately reported separately rather than merged into one number:
    /// individually checked-in visitors (named, contactable) and group-pass occupants (a headcount
    /// only — a pass admits a crew under one badge and never records who they are, so a roll call
    /// can say "4 people under ACME Contractors" and no more; pretending otherwise would be worse
    /// than saying so).
    ///
    /// Students are NOT included: the roster has no presence concept at all — Student.IsActive is
    /// roster membership ("still enrolled here"), not attendance, and inventing an attendance
    /// signal out of it would put names on an evacuation sheet that nobody ever marked present.
    /// The response says this explicitly (StudentsIncluded/StudentsNote) so a marshal reads it off
    /// the sheet instead of assuming the headcount already covers the school roll.
    ///
    /// Gated on the existing VisitorsView rather than a new permission — it is a re-presentation
    /// of the visitor log this role already reads in full, and in a real evacuation the last thing
    /// anyone needs is the person holding the phone discovering they lack a bespoke role.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors/evacuation")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(EvacuationReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvacuationReport(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var now = DateTime.UtcNow;
        var branchName = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync() ?? "";

        var onSite = await _context.Visitors.Include(v => v.VisitorProfile)
            .Where(v => v.BranchId == branchId && v.DeletedAt == null && v.Status == VisitorStatus.CheckedIn)
            .OrderBy(v => v.CheckedInAt)
            .ToListAsync();

        var activePasses = await _context.VisitorPasses
            .Where(p => p.BranchId == branchId && p.RevokedAt == null && p.ExpiresAt > now && p.CurrentVisitors > 0)
            .OrderBy(p => p.Label)
            .ToListAsync();

        var people = onSite.Select(v => new EvacuationPersonDto
        {
            VisitorId = v.Id,
            BadgeCode = v.BadgeCode,
            FullName = v.VisitorProfile!.FullName,
            Company = v.VisitorProfile.Company,
            HostName = v.HostName,
            Phone = v.VisitorProfile.Phone,
            CheckedInAt = v.CheckedInAt,
            VisitorType = v.VisitorType,
            StudentName = v.StudentName
        }).ToList();

        var passes = activePasses.Select(p => new EvacuationGroupPassDto
        {
            PassId = p.Id,
            Label = p.Label,
            OccupantCount = p.CurrentVisitors,
            ExpiresAt = p.ExpiresAt
        }).ToList();

        var passHeadcount = passes.Sum(p => p.OccupantCount);

        return Ok(new EvacuationReportDto
        {
            GeneratedAt = now,
            BranchName = branchName,
            CheckedInVisitorCount = people.Count,
            GroupPassOccupantCount = passHeadcount,
            TotalOnSite = people.Count + passHeadcount,
            StudentsIncluded = false,
            StudentsNote = "Students are not included — the roster records enrolment, not attendance, so this system has no record of which students are on site.",
            People = people,
            GroupPasses = passes
        });
    }

    /// <summary>
    /// Aggregate visitor-management report for an admin-chosen date range — summary stats, a
    /// day-by-day trend, a peak-hours breakdown, top hosts, and a "worth a second look" repeat-
    /// visitor list (the report-range equivalent of CheckInsToday/VisitsLast24Hours, just over a
    /// longer window a supervisor actually reviews). Materializes the range's visits once and
    /// aggregates in memory rather than several separate SQL group-bys — simpler to get right for
    /// a reporting endpoint that isn't a hot path, and avoids fighting EF's SQL translation of
    /// TimeSpan arithmetic (dwell time) and DateOnly grouping.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors/report")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVisitorReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var rows = await _context.Visitors
            .Where(v => v.BranchId == branchId && v.DeletedAt == null && v.CreatedAt >= rangeStart && v.CreatedAt < rangeEndExclusive)
            .Select(v => new
            {
                v.VisitorProfileId,
                ProfileFullName = v.VisitorProfile!.FullName,
                ProfilePhone = v.VisitorProfile.Phone,
                ProfileIsWatchlisted = v.VisitorProfile.IsWatchlisted,
                v.CreatedAt,
                v.CheckedInAt,
                v.CheckedOutAt,
                v.ConsentGivenAt,
                v.StudentId,
                v.HostName
            })
            .ToListAsync();

        var completed = rows.Where(r => r.CheckedInAt.HasValue).ToList();
        var withDwell = completed.Where(r => r.CheckedOutAt.HasValue).ToList();

        return Ok(new VisitorReportDto
        {
            From = fromDate,
            To = toDate,
            TotalVisits = rows.Count,
            UniqueVisitors = rows.Select(r => r.VisitorProfileId).Distinct().Count(),
            WatchlistIncidents = rows.Count(r => r.ProfileIsWatchlisted),
            RosterCheckIns = rows.Count(r => r.StudentId != null),
            AvgDwellMinutes = withDwell.Count > 0
                ? Math.Round(withDwell.Average(r => (r.CheckedOutAt!.Value - r.CheckedInAt!.Value).TotalMinutes), 1)
                : 0,
            ConsentCompliancePercent = completed.Count > 0
                ? Math.Round(100.0 * completed.Count(r => r.ConsentGivenAt.HasValue) / completed.Count, 1)
                : 0,
            VisitsByDay = rows
                .GroupBy(r => DateOnly.FromDateTime(r.CreatedAt))
                .Select(g => new DayCountDto { Day = g.Key, Count = g.Count() })
                .OrderBy(d => d.Day)
                .ToList(),
            VisitsByHour = Enumerable.Range(0, 24)
                .Select(h => new HourCountDto { Hour = h, Count = rows.Count(r => r.CreatedAt.Hour == h) })
                .ToList(),
            TopHosts = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.HostName))
                .GroupBy(r => r.HostName)
                .Select(g => new HostVisitCountDto { HostName = g.Key, Count = g.Count() })
                .OrderByDescending(h => h.Count)
                .Take(10)
                .ToList(),
            FrequentVisitors = rows
                .GroupBy(r => r.VisitorProfileId)
                .Select(g => new FrequentVisitorDto
                {
                    VisitorProfileId = g.Key,
                    FullName = g.First().ProfileFullName,
                    Phone = g.First().ProfilePhone,
                    IsWatchlisted = g.First().ProfileIsWatchlisted,
                    VisitCount = g.Count()
                })
                .Where(f => f.VisitCount >= 3)
                .OrderByDescending(f => f.VisitCount)
                .Take(20)
                .ToList()
        });
    }

    /// <summary>
    /// Raw visit log for the same range GetVisitorReport summarizes, as a CSV download — the
    /// "hand this to an auditor/inspector" artifact a dashboard chart alone can't be. Gated on
    /// ReportsExport rather than VisitorsView since it's a bulk PII export (name/phone/email per
    /// row), not just an on-screen aggregate.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors/report/export")]
    [RequirePermission(Permissions.ReportsExport)]
    public async Task<IActionResult> ExportVisitorReport(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var (fromDate, toDate, rangeStart, rangeEndExclusive) = ResolveReportRange(from, to);

        var visits = await _context.Visitors.Include(v => v.VisitorProfile)
            .Where(v => v.BranchId == branchId && v.DeletedAt == null && v.CreatedAt >= rangeStart && v.CreatedAt < rangeEndExclusive)
            .OrderBy(v => v.CreatedAt)
            .ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("Badge Code,Full Name,Phone,Email,Purpose,Host,Student,Status,Checked In,Checked Out,Watchlisted");
        foreach (var v in visits)
        {
            var p = v.VisitorProfile!;
            csv.AppendLine(string.Join(",", new[]
            {
                CsvField(v.BadgeCode), CsvField(p.FullName), CsvField(p.Phone ?? ""), CsvField(p.Email ?? ""),
                CsvField(v.Purpose), CsvField(v.HostName), CsvField(v.StudentName ?? ""), CsvField(v.Status.ToString()),
                CsvField(v.CheckedInAt?.ToString("u") ?? ""), CsvField(v.CheckedOutAt?.ToString("u") ?? ""),
                CsvField(p.IsWatchlisted ? "Yes" : "No")
            }));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"visitor-log-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.csv");
    }

    private static (DateOnly From, DateOnly To, DateTime RangeStart, DateTime RangeEndExclusive) ResolveReportRange(DateOnly? from, DateOnly? to)
    {
        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var fromDate = from ?? toDate.AddDays(-6); // default: trailing 7 days
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        // Npgsql rejects Kind=Unspecified DateTimes against "timestamp with time zone" columns —
        // DateOnly.ToDateTime always produces Unspecified, so it has to be stamped UTC explicitly
        // rather than compared directly against CreatedAt (which is already UTC).
        var rangeStart = DateTime.SpecifyKind(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var rangeEndExclusive = DateTime.SpecifyKind(toDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc).AddDays(1);
        return (fromDate, toDate, rangeStart, rangeEndExclusive);
    }

    private static string CsvField(string value)
    {
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    [HttpGet("branches/{branchId:guid}/visitors/consent-settings")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorConsentSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConsentSettings(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var settingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        return Ok(ReadConsentSettings(settingsJson));
    }

    /// <summary>
    /// Configures whether check-in requires the visitor to accept a consent/NDA statement, and
    /// what it says. Deliberately branch-scoped (stored in Branch.Settings) rather than org-wide —
    /// different sites within the same org can have different site-access agreements.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/visitors/consent-settings")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorConsentSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateConsentSettings(Guid branchId, [FromBody] VisitorConsentSettingsDto request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request.Required && string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new ProblemDetails { Title = "Consent text is required when consent is enabled", Status = StatusCodes.Status400BadRequest });

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId);
        if (branch == null) return NotFound();

        branch.Settings = WriteConsentSettings(branch.Settings, request);
        branch.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(request);
    }

    [HttpGet("branches/{branchId:guid}/visitors/visiting-day-settings")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitingDaySettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVisitingDaySettings(Guid branchId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var settingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        return Ok(ReadVisitingDaySettings(settingsJson));
    }

    /// <summary>
    /// Configures the visiting-day repeat check-in gate's threshold and whether guardians get an
    /// SMS confirming their card was used — both branch-scoped for the same reason consent
    /// settings are (different sites can want different policies).
    /// </summary>
    [HttpPut("branches/{branchId:guid}/visitors/visiting-day-settings")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitingDaySettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateVisitingDaySettings(Guid branchId, [FromBody] VisitingDaySettingsDto request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (request.CardCheckInWarningThreshold < 1)
            return BadRequest(new ProblemDetails { Title = "Threshold must be at least 1", Status = StatusCodes.Status400BadRequest });

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.Id == branchId);
        if (branch == null) return NotFound();

        branch.Settings = WriteVisitingDaySettings(branch.Settings, request);
        branch.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(request);
    }

    private const string RetentionSettingsKey = "VisitorRetention";

    internal static VisitorRetentionSettingsDto ReadRetentionSettings(string? orgSettingsJson)
    {
        if (string.IsNullOrEmpty(orgSettingsJson)) return new VisitorRetentionSettingsDto();
        try
        {
            var root = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(orgSettingsJson);
            if (root != null && root.TryGetValue(RetentionSettingsKey, out var element))
                return System.Text.Json.JsonSerializer.Deserialize<VisitorRetentionSettingsDto>(element.GetRawText()) ?? new VisitorRetentionSettingsDto();
        }
        catch (System.Text.Json.JsonException) { /* malformed settings blob — fall back to default */ }
        return new VisitorRetentionSettingsDto();
    }

    private static string WriteRetentionSettings(string? orgSettingsJson, VisitorRetentionSettingsDto retention)
    {
        var merged = string.IsNullOrEmpty(orgSettingsJson)
            ? new Dictionary<string, object>()
            : (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(orgSettingsJson) ?? new())
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        merged[RetentionSettingsKey] = retention;
        return System.Text.Json.JsonSerializer.Serialize(merged);
    }

    [HttpGet("organizations/{organizationId:guid}/visitors/retention-settings")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorRetentionSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRetentionSettings(Guid organizationId)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved) return Unauthorized();
        if (!RoleCodes.IsSuperAdmin(tenantContext.UserRole) && tenantContext.OrganizationId != organizationId) return Forbid();

        var settingsJson = await _context.Organizations.Where(o => o.Id == organizationId).Select(o => o.Settings).FirstOrDefaultAsync();
        return Ok(ReadRetentionSettings(settingsJson));
    }

    /// <summary>
    /// How long a visit record is kept before the daily VisitorRetentionJob purges it, org-wide.
    /// This is a data-minimization control, not just housekeeping — visit records carry real PII
    /// (name, phone, email, ID number, photo), so keeping them past their useful compliance
    /// window is itself a liability, not a neutral default.
    /// </summary>
    [HttpPut("organizations/{organizationId:guid}/visitors/retention-settings")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorRetentionSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateRetentionSettings(Guid organizationId, [FromBody] VisitorRetentionSettingsDto request)
    {
        var tenantContext = _tenantAccessor.TenantContext;
        if (tenantContext == null || !tenantContext.IsResolved) return Unauthorized();
        if (!RoleCodes.IsSuperAdmin(tenantContext.UserRole) && tenantContext.OrganizationId != organizationId) return Forbid();

        if (request.RetentionDays < 30 || request.RetentionDays > 3650)
            return BadRequest(new ProblemDetails { Title = "RetentionDays must be between 30 and 3650 (10 years)", Status = StatusCodes.Status400BadRequest });

        var org = await _context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        if (org == null) return NotFound();

        org.Settings = WriteRetentionSettings(org.Settings, request);
        await _context.SaveChangesAsync();

        return Ok(request);
    }

    /// <summary>
    /// Returning-visitor search by name/phone/email/ID, org-wide (a person is recognized at any
    /// branch, not just the one they're being looked up from). Deliberately a substring match
    /// anywhere in each field, not a prefix match — a front-desk search for "kamau" needs to find
    /// "Peter Kamau" by last name, a partial phone/email/ID needs to find its owner too, and none
    /// of that works with a "starts with" search. Case-insensitive throughout: FullName is
    /// lower-cased on both sides of the comparison, and the phone/email/ID fields are already
    /// normalized to one consistent case when stored (see VisitorMatching), so comparing against
    /// an identically-normalized search term is case-insensitive for free. `.Contains()` here
    /// (not a raw ILIKE '%term%' string) is deliberate too — EF Core escapes LIKE metacharacters
    /// in the pattern automatically, a raw ILIKE would need that done by hand. Capped at
    /// SearchResultLimit — this backs a live typeahead, not a report; a substring scan can't use
    /// a plain b-tree index the way the old prefix/exact-match version could, but that's an
    /// acceptable trade for a per-organization visitor list at typeahead scale.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors/search")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(List<VisitorProfileSearchResultDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchVisitorProfiles(Guid branchId, [FromQuery] string q)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Ok(new List<VisitorProfileSearchResultDto>());

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var term = q.Trim();
        var lowerTerm = term.ToLowerInvariant();
        var normEmail = VisitorMatching.NormalizeEmail(term);
        var normPhone = VisitorMatching.NormalizePhone(term);
        var normId = VisitorMatching.NormalizeIdNumber(term);

        var matches = await _context.VisitorProfiles
            .Where(p => p.OrganizationId == organizationId && p.DeletedAt == null)
            .Where(p =>
                p.FullName.ToLower().Contains(lowerTerm) ||
                (normEmail != null && p.NormalizedEmail != null && p.NormalizedEmail.Contains(normEmail)) ||
                (normPhone != null && p.NormalizedPhone != null && p.NormalizedPhone.Contains(normPhone)) ||
                (normId != null && p.NormalizedIdNumber != null && p.NormalizedIdNumber.Contains(normId)))
            .OrderBy(p => p.FullName)
            .Take(SearchResultLimit)
            .ToListAsync();

        if (matches.Count == 0) return Ok(new List<VisitorProfileSearchResultDto>());

        var profileIds = matches.Select(p => p.Id).ToList();
        var stats = await GetStatsAsync(profileIds);
        var lastVisitByProfile = await _context.Visitors
            .Where(v => profileIds.Contains(v.VisitorProfileId) && v.DeletedAt == null)
            .GroupBy(v => v.VisitorProfileId)
            .Select(g => new { ProfileId = g.Key, LastVisitAt = g.Max(v => v.CreatedAt) })
            .ToDictionaryAsync(x => x.ProfileId, x => x.LastVisitAt);
        var activeProfileIds = (await _context.Visitors
            .Where(v => profileIds.Contains(v.VisitorProfileId) && v.DeletedAt == null && v.Status == VisitorStatus.CheckedIn)
            .Select(v => v.VisitorProfileId)
            .ToListAsync()).ToHashSet();

        var results = matches.Select(p =>
        {
            var s = stats.GetValueOrDefault(p.Id);
            return new VisitorProfileSearchResultDto
            {
                Id = p.Id,
                FullName = p.FullName,
                Phone = p.Phone,
                Email = p.Email,
                Company = p.Company,
                IdNumber = p.IdNumber,
                PhotoUrl = p.PhotoUrl,
                IsWatchlisted = p.IsWatchlisted,
                WatchlistReason = p.WatchlistReason,
                LastVisitAt = lastVisitByProfile.GetValueOrDefault(p.Id),
                TotalVisits = s.Total,
                VisitsLast24Hours = s.Last24h,
                HasActiveVisit = activeProfileIds.Contains(p.Id)
            };
        }).ToList();

        return Ok(results);
    }

    [HttpGet("branches/{branchId:guid}/visitors/{visitorId:guid}")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVisitor(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);

        if (visitor == null) return NotFound();
        var stats = await GetStatsAsync(new[] { visitor.VisitorProfileId });
        return Ok(MapToDto(visitor, visitor.VisitorProfile!, stats.GetValueOrDefault(visitor.VisitorProfileId)));
    }

    /// <summary>
    /// Reissues a fresh signed QR token for an already-checked-in visit — used when staff reopen
    /// the badge view later rather than right after check-in (the token from the original
    /// check-in response isn't persisted anywhere, by design; a new one is just as valid).
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/{visitorId:guid}/badge-token")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReissueBadgeToken(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
        if (visitor == null) return NotFound();

        if (visitor.Status != VisitorStatus.CheckedIn)
            return BadRequest(new ProblemDetails { Title = "Only an active (checked-in) visit has a badge", Status = StatusCodes.Status400BadRequest });

        var stats = await GetStatsAsync(new[] { visitor.VisitorProfileId });
        var qrToken = _badgeTokenService.IssueVisitToken(visitor.Id, branchId, DateTime.UtcNow.Add(BadgeValidity));
        return Ok(MapToDto(visitor, visitor.VisitorProfile!, stats.GetValueOrDefault(visitor.VisitorProfileId), qrToken));
    }

    /// <summary>
    /// Uploads a check-in photo (captured client-side from the camera) and returns its stored
    /// URL. Standalone from any particular visitor/profile — the photo is taken before the
    /// check-in form is submitted, so nothing to attach it to exists yet; the caller passes the
    /// returned URL back as PhotoUrl on the actual check-in request.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/photo")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [RequestSizeLimit(MaxPhotoSizeBytes)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadPhoto(Guid branchId, IFormFile file)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (file == null || file.Length == 0)
            return BadRequest(new ProblemDetails { Title = "No photo was provided", Status = StatusCodes.Status400BadRequest });
        if (file.Length > MaxPhotoSizeBytes)
            return BadRequest(new ProblemDetails { Title = $"Photo exceeds the {MaxPhotoSizeBytes / 1024 / 1024}MB size limit", Status = StatusCodes.Status400BadRequest });

        var mimeType = file.ContentType ?? "";
        if (!mimeType.StartsWith("image/"))
            return BadRequest(new ProblemDetails { Title = "Only image files are accepted", Status = StatusCodes.Status400BadRequest });

        await using var uploadStream = file.OpenReadStream();
        var uploadResult = await _mediaStorage.UploadAsync(uploadStream, file.FileName, mimeType);
        if (!uploadResult.Success)
        {
            _logger.LogError("Visitor photo upload failed for branch {BranchId}: {Error}", branchId, uploadResult.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "Failed to store the photo" });
        }

        return Ok(uploadResult.FileUrl);
    }

    /// <summary>
    /// Pre-register a visitor ahead of their arrival (Status = PreRegistered).
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/pre-register")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> PreRegister(Guid branchId, [FromBody] PreRegisterVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        var organizationId = await ResolveOrganizationIdAsync(branchId);

        Visitor visitor = null!;
        VisitorProfile profile = null!;
        // Npgsql's retrying execution strategy (EnableRetryOnFailure) doesn't allow a raw
        // BeginTransactionAsync — the whole retriable unit must go through
        // CreateExecutionStrategy().ExecuteAsync, same requirement as IUnitOfWork.ExecuteInTransactionAsync
        // uses elsewhere in this codebase (CreateTokenCommandHandler etc.).
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            profile = await FindOrCreateProfileAsync(organizationId, request.VisitorProfileId,
                request.FullName, request.Phone, request.Email, request.IdNumber, request.Company, null);

            var badgeCode = await GenerateBadgeCodeAsync(branchId);
            visitor = new Visitor
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                VisitorProfileId = profile.Id,
                BadgeCode = badgeCode,
                Purpose = request.Purpose,
                VehiclePlate = request.VehiclePlate,
                HostUserId = request.HostUserId,
                HostName = request.HostName,
                Status = VisitorStatus.PreRegistered,
                VisitorType = request.VisitorType,
                ScheduledAt = request.ScheduledAt.HasValue ? ToUtc(request.ScheduledAt.Value) : null,
                PreRegisteredByUserId = CurrentUserId(),
                Notes = request.Notes
            };
            _context.Visitors.Add(visitor);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        var stats = await GetStatsAsync(new[] { profile.Id });
        var dto = MapToDto(visitor, profile, stats.GetValueOrDefault(profile.Id));
        await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.PreRegistered, dto);
        return CreatedAtAction(nameof(GetVisitor), new { branchId, visitorId = visitor.Id }, dto);
    }

    /// <summary>
    /// Books one or many EXPECTED visitors in for a future date/time (Status = Expected). One
    /// request covers a whole party arriving together for the same reason — the realistic shape of
    /// a pre-booking (an interview panel, a contractor crew, a governors' meeting) — instead of
    /// making reception retype the host, purpose and time once per name.
    ///
    /// Every person still becomes an ordinary Visitor row against an ordinary VisitorProfile,
    /// matched through the same FindOrCreateProfileAsync every other entry point uses, so a
    /// pre-registered arrival is not a parallel kind of record that later has to be reconciled —
    /// it converts into a real check-in through the existing CheckInExisting action (which keeps
    /// every consent, watchlist and repeat-check-in control intact), it doesn't get duplicated
    /// into one.
    ///
    /// ScheduledAt is populated alongside ExpectedArrivalAt on purpose: several existing queries
    /// (GetVisitors' default "today" view, GetSummary) already key off ScheduledAt, so filling
    /// both means an expected arrival shows up on the front desk's normal screens on the day
    /// without touching any of them.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/expected")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(List<VisitorDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateExpectedVisitors(Guid branchId, [FromBody] CreateExpectedVisitorsRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var entries = (request.Visitors ?? new List<ExpectedVisitorEntry>())
            .Where(e => !string.IsNullOrWhiteSpace(e.FullName))
            .ToList();

        if (entries.Count == 0)
            return BadRequest(new ProblemDetails { Title = "At least one visitor with a name is required", Status = StatusCodes.Status400BadRequest });
        if (entries.Count > MaxExpectedBatchSize)
            return BadRequest(new ProblemDetails { Title = $"At most {MaxExpectedBatchSize} visitors can be pre-registered in one request", Status = StatusCodes.Status400BadRequest });
        if (string.IsNullOrWhiteSpace(request.Purpose))
            return BadRequest(new ProblemDetails { Title = "Purpose of visit is required", Status = StatusCodes.Status400BadRequest });
        if (request.ExpectedArrivalAt == default)
            return BadRequest(new ProblemDetails { Title = "An expected arrival date and time is required", Status = StatusCodes.Status400BadRequest });

        var expectedUtc = ToUtc(request.ExpectedArrivalAt);
        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var createdByUserId = CurrentUserId();

        var created = new List<(Visitor Visit, VisitorProfile Profile)>();
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            created.Clear(); // a retried execution must not accumulate the previous attempt's rows
            await using var transaction = await _context.Database.BeginTransactionAsync();

            foreach (var entry in entries)
            {
                var profile = await FindOrCreateProfileAsync(organizationId, entry.VisitorProfileId,
                    entry.FullName, entry.Phone, entry.Email, entry.IdNumber, entry.Company, null);
                await _context.SaveChangesAsync(); // each profile needs an Id before the visit references it

                var visitor = new Visitor
                {
                    OrganizationId = organizationId,
                    BranchId = branchId,
                    VisitorProfileId = profile.Id,
                    BadgeCode = await GenerateBadgeCodeAsync(branchId),
                    Purpose = request.Purpose,
                    VehiclePlate = entry.VehiclePlate,
                    HostUserId = request.HostUserId,
                    HostName = request.HostName,
                    Status = VisitorStatus.Expected,
                    VisitorType = request.VisitorType,
                    ExpectedArrivalAt = expectedUtc,
                    ScheduledAt = expectedUtc,
                    PreRegisteredByUserId = createdByUserId,
                    Notes = request.Notes
                };
                _context.Visitors.Add(visitor);
                await _context.SaveChangesAsync();
                created.Add((visitor, profile));
            }

            await transaction.CommitAsync();
        });

        var stats = await GetStatsAsync(created.Select(c => c.Profile.Id));
        var dtos = created
            .Select(c => MapToDto(c.Visit, c.Profile, stats.GetValueOrDefault(c.Profile.Id)))
            .ToList();

        foreach (var dto in dtos)
            await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.PreRegistered, dto);

        return StatusCode(StatusCodes.Status201Created, dtos);
    }

    /// <summary>
    /// Expected arrivals still outstanding over a date range (defaults to today through a week
    /// out). Covers BOTH pre-registration statuses — a front desk asking "who's due?" doesn't care
    /// whether a booking came from the older PreRegister path or the expected-arrivals batch.
    /// Anything already checked in, checked out or cancelled has stopped being an expectation and
    /// drops out by construction.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors/expected")]
    [RequirePermission(Permissions.VisitorsView)]
    [ProducesResponseType(typeof(List<VisitorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetExpectedVisitors(Guid branchId, [FromQuery] DateOnly? from = null, [FromQuery] DateOnly? to = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var toDate = to ?? fromDate.AddDays(7);
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        // Same Kind=Unspecified trap as ResolveReportRange — DateOnly.ToDateTime always produces
        // Unspecified, which Npgsql refuses against a timestamptz column.
        var rangeStart = DateTime.SpecifyKind(fromDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var rangeEndExclusive = DateTime.SpecifyKind(toDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc).AddDays(1);

        var expected = await _context.Visitors.Include(v => v.VisitorProfile)
            .Where(v => v.BranchId == branchId && v.DeletedAt == null &&
                        (v.Status == VisitorStatus.Expected || v.Status == VisitorStatus.PreRegistered))
            // COALESCE: an Expected row always has ExpectedArrivalAt, an older PreRegistered row
            // only ever had ScheduledAt — one ordering/filter key covers both.
            .Where(v => (v.ExpectedArrivalAt ?? v.ScheduledAt) >= rangeStart &&
                        (v.ExpectedArrivalAt ?? v.ScheduledAt) < rangeEndExclusive)
            .OrderBy(v => v.ExpectedArrivalAt ?? v.ScheduledAt)
            .ToListAsync();

        var stats = await GetStatsAsync(expected.Select(v => v.VisitorProfileId));
        return Ok(expected.Select(v => MapToDto(v, v.VisitorProfile!, stats.GetValueOrDefault(v.VisitorProfileId))).ToList());
    }

    /// <summary>
    /// Cancels an expected/pre-registered arrival that isn't coming. Deliberately a status change
    /// rather than a delete — "they were booked in and didn't come" is itself a fact a visitor log
    /// should be able to answer, and DeleteVisitor exists (with its mandatory reason) for records
    /// that genuinely shouldn't be there at all.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/{visitorId:guid}/cancel")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelExpectedVisitor(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
        if (visitor == null) return NotFound();

        if (visitor.Status != VisitorStatus.Expected && visitor.Status != VisitorStatus.PreRegistered)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot cancel",
                Detail = $"This visit is already '{visitor.Status}' — only an expected arrival that hasn't started yet can be cancelled.",
                Status = StatusCodes.Status400BadRequest
            });

        visitor.Status = VisitorStatus.Cancelled;
        visitor.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var stats = await GetStatsAsync(new[] { visitor.VisitorProfileId });
        return Ok(MapToDto(visitor, visitor.VisitorProfile!, stats.GetValueOrDefault(visitor.VisitorProfileId)));
    }

    /// <summary>
    /// Walk-in check-in: creates and checks in a visitor in one step.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/checkin")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckIn(Guid branchId, [FromBody] CheckInVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new ProblemDetails { Title = "Full name is required", Status = StatusCodes.Status400BadRequest });

        var branchSettingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var consentSettings = ReadConsentSettings(branchSettingsJson);
        if (consentSettings.Required && !request.ConsentGiven)
            return BadRequest(new ProblemDetails { Title = "Visitor consent is required to check in", Status = StatusCodes.Status400BadRequest });
        var visitingDaySettings = ReadVisitingDaySettings(branchSettingsJson);

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var (studentId, studentName, defaultHostName, defaultPurpose) = await ResolveStudentAsync(request.StudentId, branchId);

        Visitor visitor = null!;
        VisitorProfile profile = null!;
        var strategy = _context.Database.CreateExecutionStrategy();
        IActionResult? conflict = null;
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();

            profile = await FindOrCreateProfileAsync(organizationId, request.VisitorProfileId,
                request.FullName, request.Phone, request.Email, request.IdNumber, request.Company, request.PhotoUrl);
            await _context.SaveChangesAsync(); // profile needs an Id before the active-visit check below

            var activeVisit = await GetActiveVisitAsync(profile.Id);
            if (activeVisit != null)
            {
                conflict = Conflict(new ProblemDetails
                {
                    Title = "Already checked in",
                    Detail = $"{profile.FullName} already has an active visit (badge {activeVisit.BadgeCode}, checked in {activeVisit.CheckedInAt:HH:mm} at {activeVisit.Branch?.Name}). They must check out before starting a new visit.",
                    Status = StatusCodes.Status409Conflict
                });
                await transaction.RollbackAsync();
                return;
            }

            // WATCHLIST: checked before anything else about this visit is decided — a flagged
            // person is refused entry outright, not warned about after the fact.
            var watchlistError = WatchlistBlock(profile, request.WatchlistOverrideReason);
            if (watchlistError != null)
            {
                conflict = watchlistError;
                await transaction.RollbackAsync();
                return;
            }

            // Visiting-day repeat check-in gate: only applies to roster (StudentId-linked)
            // check-ins — a plain walk-in visitor isn't part of the guardian-card abuse scenario
            // this exists for. A Manager+ user's own elevated login IS the "supervisor override";
            // a Staff/Viewer user must flag the card first (StudentGuardianSearchResultDto's
            // CheckInsToday already showed them this same count before they picked this guardian,
            // so the gate here can never surprise them with a number they haven't already seen).
            if (studentId.HasValue)
            {
                var checkInsToday = await GetCheckInsTodayAsync(profile.Id);
                // The `!profile.IsWatchlisted` term this condition used to carry is gone on
                // purpose: a watchlisted profile can no longer reach this line at all (the block
                // above stops it), unless a Manager overrode — in which case IsManagerOrAbove
                // already skips the gate. Its replacement is CardFlagReason: supplying one is the
                // front-desk Staff self-service path, and it now flags the card as part of THIS
                // request instead of requiring a separate flag call first (which the watchlist
                // block would otherwise turn into a self-inflicted lockout).
                if (checkInsToday >= visitingDaySettings.CardCheckInWarningThreshold
                    && string.IsNullOrWhiteSpace(request.CardFlagReason)
                    && !RoleCodes.IsManagerOrAbove(_tenantAccessor.TenantContext!.UserRole))
                {
                    conflict = BadRequest(new ProblemDetails
                    {
                        Title = "This card has already been used today",
                        Detail = $"{profile.FullName}'s card has already been checked in {checkInsToday} time(s) today. Flag the card with a reason to proceed, or ask a Manager/Admin to complete this check-in.",
                        Status = StatusCodes.Status400BadRequest
                    });
                    await transaction.RollbackAsync();
                    return;
                }
            }

            // Contractor induction recorded at the desk — written through to the PERSON so the
            // next visit (and any other branch) already knows. Null means "don't touch it".
            if (request.InductionCompletedAt.HasValue)
            {
                profile.InductionCompletedAt = ToUtc(request.InductionCompletedAt.Value);
                if (!string.IsNullOrWhiteSpace(request.InductionNotes)) profile.InductionNotes = request.InductionNotes;
                profile.UpdatedAt = DateTime.UtcNow;
            }

            var badgeCode = await GenerateBadgeCodeAsync(branchId);
            visitor = new Visitor
            {
                OrganizationId = organizationId,
                BranchId = branchId,
                VisitorProfileId = profile.Id,
                BadgeCode = badgeCode,
                Purpose = string.IsNullOrWhiteSpace(request.Purpose) ? (defaultPurpose ?? request.Purpose) : request.Purpose,
                VehiclePlate = request.VehiclePlate,
                HostUserId = request.HostUserId,
                HostName = string.IsNullOrWhiteSpace(request.HostName) ? (defaultHostName ?? request.HostName) : request.HostName,
                StudentId = studentId,
                StudentName = studentName,
                Status = VisitorStatus.CheckedIn,
                VisitorType = request.VisitorType ?? VisitorType.Guest,
                CheckedInAt = DateTime.UtcNow,
                ConsentGivenAt = consentSettings.Required ? DateTime.UtcNow : null,
                Notes = ComposeCheckInNotes(request.Notes, request.WatchlistOverrideReason, profile.IsWatchlisted)
            };
            _context.Visitors.Add(visitor);

            // Flag the card as part of the same transaction as the visit it authorised — either
            // both happen or neither does, so there is never a check-in whose stated justification
            // was "I flagged the card" with no flag to show for it.
            if (!string.IsNullOrWhiteSpace(request.CardFlagReason) && !profile.IsWatchlisted)
                ApplyWatchlist(profile, true, request.CardFlagReason);

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        });

        if (conflict != null) return conflict;

        await NotifyHostAsync(visitor, profile, organizationId, branchId);
        await NotifyGuardianAsync(visitor, profile, organizationId, visitingDaySettings);

        var stats = await GetStatsAsync(new[] { profile.Id });
        var qrToken = _badgeTokenService.IssueVisitToken(visitor.Id, branchId, DateTime.UtcNow.Add(BadgeValidity));
        var dto = MapToDto(visitor, profile, stats.GetValueOrDefault(profile.Id), qrToken, InductionWarning(visitor, profile));
        await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.CheckedIn, dto);

        return CreatedAtAction(nameof(GetVisitor), new { branchId, visitorId = visitor.Id }, dto);
    }

    /// <summary>
    /// A Manager's watchlist override has to survive in the record itself, not just in a log line —
    /// the visit's own Notes is where anyone reviewing that visit will actually look.
    /// </summary>
    private static string? ComposeCheckInNotes(string? notes, string? overrideReason, bool wasWatchlisted)
    {
        if (!wasWatchlisted || string.IsNullOrWhiteSpace(overrideReason)) return notes;
        var line = $"[Watchlist override] {overrideReason}";
        return string.IsNullOrWhiteSpace(notes) ? line : $"{notes}\n{line}";
    }

    /// <summary>
    /// Checks in a previously pre-registered visitor on arrival.
    /// </summary>
    [HttpPost("branches/{branchId:guid}/visitors/{visitorId:guid}/checkin")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckInExisting(Guid branchId, Guid visitorId, [FromBody] CheckInVisitorRequest? request = null)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);

        if (visitor == null) return NotFound();
        var profile = visitor.VisitorProfile!;

        // Both pre-registration statuses convert through this one action — that's the whole point
        // of Expected being a status on the existing record rather than a separate booking table:
        // "convert to a check-in" is just this, with every control below applying unchanged.
        if (visitor.Status != VisitorStatus.PreRegistered && visitor.Status != VisitorStatus.Expected)
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot check in",
                Detail = $"Visitor is already '{visitor.Status}', not an expected arrival.",
                Status = StatusCodes.Status400BadRequest
            });

        var watchlistError = WatchlistBlock(profile, request?.WatchlistOverrideReason);
        if (watchlistError != null) return watchlistError;

        var activeVisit = await GetActiveVisitAsync(profile.Id, excludeVisitId: visitor.Id);
        if (activeVisit != null)
            return Conflict(new ProblemDetails
            {
                Title = "Already checked in",
                Detail = $"{profile.FullName} already has an active visit (badge {activeVisit.BadgeCode}) at {activeVisit.Branch?.Name}. They must check out before starting a new visit.",
                Status = StatusCodes.Status409Conflict
            });

        var branchSettingsJson = await _context.Branches.Where(b => b.Id == branchId).Select(b => b.Settings).FirstOrDefaultAsync();
        var consentSettings = ReadConsentSettings(branchSettingsJson);
        if (consentSettings.Required && !(request?.ConsentGiven ?? false))
            return BadRequest(new ProblemDetails { Title = "Visitor consent is required to check in", Status = StatusCodes.Status400BadRequest });
        var visitingDaySettings = ReadVisitingDaySettings(branchSettingsJson);

        if (visitor.StudentId.HasValue)
        {
            var checkInsToday = await GetCheckInsTodayAsync(profile.Id);
            // See the identical gate in CheckIn for why the old `!profile.IsWatchlisted` term is
            // gone and CardFlagReason replaces the separate flag-then-check-in client dance.
            if (checkInsToday >= visitingDaySettings.CardCheckInWarningThreshold
                && string.IsNullOrWhiteSpace(request?.CardFlagReason)
                && !RoleCodes.IsManagerOrAbove(_tenantAccessor.TenantContext!.UserRole))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "This card has already been used today",
                    Detail = $"{profile.FullName}'s card has already been checked in {checkInsToday} time(s) today. Flag the card with a reason to proceed, or ask a Manager/Admin to complete this check-in.",
                    Status = StatusCodes.Status400BadRequest
                });
            }
        }

        if (request != null)
        {
            if (!string.IsNullOrWhiteSpace(request.IdNumber) && string.IsNullOrWhiteSpace(profile.IdNumber))
            {
                profile.IdNumber = request.IdNumber;
                profile.NormalizedIdNumber = VisitorMatching.NormalizeIdNumber(request.IdNumber);
            }
            if (!string.IsNullOrWhiteSpace(request.PhotoUrl)) profile.PhotoUrl = request.PhotoUrl;
            if (!string.IsNullOrWhiteSpace(request.VehiclePlate)) visitor.VehiclePlate = request.VehiclePlate;

            // Nullable on the request specifically so "the caller said nothing" keeps whatever the
            // pre-registered record already carries, rather than silently resetting it to Guest.
            if (request.VisitorType is { } requestedType) visitor.VisitorType = requestedType;

            if (request.InductionCompletedAt.HasValue)
            {
                profile.InductionCompletedAt = ToUtc(request.InductionCompletedAt.Value);
                if (!string.IsNullOrWhiteSpace(request.InductionNotes)) profile.InductionNotes = request.InductionNotes;
            }
        }

        // Watchlist matching happens on the profile itself (FindOrCreateProfileAsync /
        // SetWatchlist keep it current) and is enforced by the WatchlistBlock call above, which
        // has already refused this check-in if the person is flagged.

        var wasWatchlisted = profile.IsWatchlisted;

        visitor.Status = VisitorStatus.CheckedIn;
        visitor.CheckedInAt = DateTime.UtcNow;
        visitor.ConsentGivenAt = consentSettings.Required ? DateTime.UtcNow : null;
        visitor.Notes = ComposeCheckInNotes(visitor.Notes, request?.WatchlistOverrideReason, wasWatchlisted);
        visitor.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request?.CardFlagReason) && !wasWatchlisted)
            ApplyWatchlist(profile, true, request!.CardFlagReason);

        profile.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await NotifyHostAsync(visitor, profile, visitor.OrganizationId, branchId);
        await NotifyGuardianAsync(visitor, profile, visitor.OrganizationId, visitingDaySettings);

        var stats = await GetStatsAsync(new[] { profile.Id });
        var qrToken = _badgeTokenService.IssueVisitToken(visitor.Id, branchId, DateTime.UtcNow.Add(BadgeValidity));
        var dto = MapToDto(visitor, profile, stats.GetValueOrDefault(profile.Id), qrToken, InductionWarning(visitor, profile));
        await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.CheckedIn, dto);

        return Ok(dto);
    }

    [HttpPost("branches/{branchId:guid}/visitors/{visitorId:guid}/checkout")]
    [RequirePermission(Permissions.VisitorsCheckOut)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CheckOut(Guid branchId, Guid visitorId)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        // CONCURRENCY: atomic conditional update, same pattern as FeedbackController's
        // double-submit guard — only a visitor still CheckedIn can transition to CheckedOut,
        // checked as part of the same UPDATE rather than read-then-write.
        var affected = await _context.Visitors
            .Where(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null && v.Status == VisitorStatus.CheckedIn)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, VisitorStatus.CheckedOut)
                .SetProperty(v => v.CheckedOutAt, DateTime.UtcNow)
                .SetProperty(v => v.UpdatedAt, DateTime.UtcNow));

        if (affected == 0)
        {
            var exists = await _context.Visitors.AnyAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
            if (!exists) return NotFound();
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot check out",
                Detail = "Visitor is not currently checked in.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile).FirstAsync(v => v.Id == visitorId);
        var stats = await GetStatsAsync(new[] { visitor.VisitorProfileId });
        var dto = MapToDto(visitor, visitor.VisitorProfile!, stats.GetValueOrDefault(visitor.VisitorProfileId));
        await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.CheckedOut, dto);
        return Ok(dto);
    }

    [HttpPut("branches/{branchId:guid}/visitors/{visitorId:guid}")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateVisitor(Guid branchId, Guid visitorId, [FromBody] UpdateVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
        if (visitor == null) return NotFound();
        var profile = visitor.VisitorProfile!;

        // Identity fields belong to the profile now — editing them here edits the person's
        // record, which is exactly what should happen (a corrected phone number should apply
        // to their next visit too, not just this one).
        profile.FullName = request.FullName;
        profile.Phone = request.Phone;
        profile.NormalizedPhone = VisitorMatching.NormalizePhone(request.Phone);
        profile.Email = request.Email;
        profile.NormalizedEmail = VisitorMatching.NormalizeEmail(request.Email);
        profile.Company = request.Company;
        profile.IdNumber = request.IdNumber;
        profile.NormalizedIdNumber = VisitorMatching.NormalizeIdNumber(request.IdNumber);
        profile.UpdatedAt = DateTime.UtcNow;

        visitor.Purpose = request.Purpose;
        visitor.VehiclePlate = request.VehiclePlate;
        visitor.HostUserId = request.HostUserId;
        visitor.HostName = request.HostName;
        visitor.Notes = request.Notes;
        visitor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        var stats = await GetStatsAsync(new[] { profile.Id });
        return Ok(MapToDto(visitor, profile, stats.GetValueOrDefault(profile.Id)));
    }

    [HttpPut("branches/{branchId:guid}/visitors/{visitorId:guid}/watchlist")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(VisitorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetWatchlist(Guid branchId, Guid visitorId, [FromBody] SetWatchlistRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
        if (visitor == null) return NotFound();

        if (request.IsWatchlisted && string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ProblemDetails
            {
                Title = "A reason is required to add a visitor to the watchlist",
                Status = StatusCodes.Status400BadRequest
            });

        // Flagging is a property of the PERSON, not this one visit — it follows the profile to
        // every branch and every future visit, and now BARS them from checking in at any of them
        // (VisitorsController.WatchlistBlock) rather than merely marking their record.
        var profile = visitor.VisitorProfile!;
        ApplyWatchlist(profile, request.IsWatchlisted, request.Reason);

        await _context.SaveChangesAsync();
        var stats = await GetStatsAsync(new[] { profile.Id });
        var dto = MapToDto(visitor, profile, stats.GetValueOrDefault(profile.Id));
        await _activityBroadcaster.BroadcastAsync(branchId, request.IsWatchlisted ? VisitorActivityKind.Flagged : VisitorActivityKind.Unflagged, dto);
        return Ok(dto);
    }

    /// <summary>
    /// Same flagging action as SetWatchlist, but reached by VisitorProfileId directly instead of
    /// an existing visit row — needed by the "Flag &amp; Check In" path in the visiting-day repeat
    /// check-in gate (VisitorsController.CheckIn), where staff need to flag a guardian's card
    /// BEFORE today's visit row exists yet, not after.
    ///
    /// Deliberately gated on VisitorsCheckIn, not VisitorsManage like the general per-visit
    /// SetWatchlist above — Staff (who have VisitorsCheckIn but not VisitorsManage) are exactly
    /// who needs to call this: they're the tier the repeat-check-in gate is written to stop from
    /// self-approving, and the ONLY self-service way past it (short of a Manager+ login) is to
    /// flag the card themselves. Gating this the same as the general watchlist toggle would make
    /// that path silently 403 for the one role it exists for.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/visitor-profiles/{profileId:guid}/watchlist")]
    [RequirePermission(Permissions.VisitorsCheckIn)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetProfileWatchlist(Guid branchId, Guid profileId, [FromBody] SetWatchlistRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var profile = await _context.VisitorProfiles.FirstOrDefaultAsync(
            p => p.Id == profileId && p.OrganizationId == organizationId && p.DeletedAt == null);
        if (profile == null) return NotFound();

        if (request.IsWatchlisted && string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ProblemDetails
            {
                Title = "A reason is required to add a visitor to the watchlist",
                Status = StatusCodes.Status400BadRequest
            });

        ApplyWatchlist(profile, request.IsWatchlisted, request.Reason);
        await _context.SaveChangesAsync();

        return Ok(new { profile.Id, profile.IsWatchlisted, profile.WatchlistReason, profile.WatchlistAddedAt });
    }

    /// <summary>
    /// Records (or clears) a person's contractor site induction. On the PROFILE, so it's checked
    /// at the moment of check-in — before this trip's Visitor row exists — and so it follows them
    /// to every branch, which is what an induction actually is.
    ///
    /// Gated on VisitorsManage, not VisitorsCheckIn: an induction date is the thing the
    /// contractor-check-in warning is measured against, so letting the same front-desk tier that
    /// sees the warning also silence it by typing a date would make the control decorative. Staff
    /// can still record one inline at check-in for a person who has none — CheckIn's
    /// InductionCompletedAt — but only a Manager/Admin can revise or revoke an existing one here.
    /// </summary>
    [HttpPut("branches/{branchId:guid}/visitor-profiles/{profileId:guid}/induction")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetInduction(Guid branchId, Guid profileId, [FromBody] SetInductionRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var organizationId = await ResolveOrganizationIdAsync(branchId);
        var profile = await _context.VisitorProfiles.FirstOrDefaultAsync(
            p => p.Id == profileId && p.OrganizationId == organizationId && p.DeletedAt == null);
        if (profile == null) return NotFound();

        if (request.CompletedAt.HasValue && ToUtc(request.CompletedAt.Value) > DateTime.UtcNow.AddDays(1))
            return BadRequest(new ProblemDetails
            {
                Title = "Induction date cannot be in the future",
                Status = StatusCodes.Status400BadRequest
            });

        profile.InductionCompletedAt = request.CompletedAt.HasValue ? ToUtc(request.CompletedAt.Value) : null;
        profile.InductionNotes = request.CompletedAt.HasValue ? request.Notes : null;
        profile.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            profile.Id,
            profile.InductionCompletedAt,
            profile.InductionNotes,
            InductionExpiresAt = profile.InductionCompletedAt?.AddDays(InductionValidityDays),
            InductionValid = IsInductionValid(profile)
        });
    }

    /// <summary>
    /// Soft-deletes a visit record — a reason is mandatory. Visitor logs are a compliance
    /// artifact in their own right (evacuation rosters, incident/investigation lookback), so a
    /// hard delete would erase exactly what the log exists to prove; deleting instead hides the
    /// row from normal views while keeping who deleted it, when, and why. Only the visit is
    /// removed — the person's profile (and their other visits) is untouched.
    /// </summary>
    [HttpDelete("branches/{branchId:guid}/visitors/{visitorId:guid}")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteVisitor(Guid branchId, Guid visitorId, [FromBody] DeleteVisitorRequest request)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ProblemDetails { Title = "A reason is required to delete a visitor record", Status = StatusCodes.Status400BadRequest });

        var visitor = await _context.Visitors.Include(v => v.VisitorProfile)
            .FirstOrDefaultAsync(v => v.Id == visitorId && v.BranchId == branchId && v.DeletedAt == null);
        if (visitor == null) return NotFound();

        visitor.DeletedAt = DateTime.UtcNow;
        visitor.DeletedByUserId = CurrentUserId();
        visitor.DeletionReason = request.Reason;
        visitor.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        var stats = await GetStatsAsync(new[] { visitor.VisitorProfileId });
        await _activityBroadcaster.BroadcastAsync(branchId, VisitorActivityKind.Deleted,
            MapToDto(visitor, visitor.VisitorProfile!, stats.GetValueOrDefault(visitor.VisitorProfileId)));
        return NoContent();
    }

    /// <summary>
    /// Compliance audit view — every deleted visit record for this branch, with who deleted it,
    /// when, and why. The records themselves were never actually removed (see DeleteVisitor);
    /// this just surfaces them without going to the database directly.
    /// </summary>
    [HttpGet("branches/{branchId:guid}/visitors/deleted")]
    [RequirePermission(Permissions.VisitorsManage)]
    [ProducesResponseType(typeof(List<DeletedVisitorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDeletedVisitors(Guid branchId, [FromQuery] int limit = 100)
    {
        var branchError = await VerifyBranchOwnership(branchId);
        if (branchError != null) return branchError;

        var deleted = await _context.Visitors.Include(v => v.VisitorProfile)
            .Where(v => v.BranchId == branchId && v.DeletedAt != null)
            .OrderByDescending(v => v.DeletedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync();

        var deleterIds = deleted.Select(v => v.DeletedByUserId).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var deleterNames = await _context.Users
            .Where(u => deleterIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());

        var results = deleted.Select(v => new DeletedVisitorDto
        {
            Id = v.Id,
            BadgeCode = v.BadgeCode,
            FullName = v.VisitorProfile!.FullName,
            Purpose = v.Purpose,
            StatusAtDeletion = v.Status,
            CreatedAt = v.CreatedAt,
            DeletedAt = v.DeletedAt!.Value,
            DeletedByUserName = v.DeletedByUserId.HasValue ? deleterNames.GetValueOrDefault(v.DeletedByUserId.Value) : null,
            DeletionReason = v.DeletionReason ?? ""
        }).ToList();

        return Ok(results);
    }
}
