using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// The shared computations behind staff self-service configuration. The controller owns the HTTP and
/// the permission gates; everything a caller could get WRONG lives here, in one place, so the grid a
/// teacher reads and the claim the server accepts cannot disagree about what "free" means.
///
/// NOTHING HERE RE-DERIVES A CLASH RULE. TimetableChecker is the one home for that, and it is what
/// the timetable master, the publish gate and the nightly sweep all use. What this adds is the
/// narrower question the checker never had to answer: not "is this timetable sound" but "may THIS
/// person put THIS lesson HERE, right now".
/// </summary>
public static class StaffSelfService
{
    /// <summary>Beyond this a teacher is asked to talk to the timetable master instead of declaring more.</summary>
    public const int DefaultMaxUnavailabilityLines = 40;

    // =================================================================================================
    // The fingerprint
    // =================================================================================================

    /// <summary>
    /// WHAT THE READER BELIEVED. Every lesson that could make a slot unavailable to this caller,
    /// reduced to one short string: the timetable, its lesson set, and the caller's own unavailability.
    ///
    /// A claim sends it back and the server re-reads under the lock. If it has moved, the claim is
    /// REFUSED and the fresh grid is returned — rather than applying the claim to a slot the teacher
    /// never saw. Optimistic concurrency, and the only thing that makes a "pick a free slot" interface
    /// honest when forty people are picking at once.
    ///
    /// It is a hash of ids and slots, never of names: two readers of the same grid with different
    /// permission to see names must produce the SAME fingerprint, or a master and a teacher could
    /// never be looking at the same grid.
    /// </summary>
    public static string Fingerprint(Guid timetableId, IEnumerable<TimetableLesson> lessons, IEnumerable<TeacherUnavailabilityDto> mine)
    {
        var sb = new StringBuilder();
        sb.Append(timetableId.ToString("N"));
        foreach (var l in lessons.OrderBy(l => l.CycleDay).ThenBy(l => l.PeriodKey, StringComparer.Ordinal).ThenBy(l => l.Id))
        {
            sb.Append('|').Append(l.CycleDay).Append(':').Append(l.PeriodKey).Append(':')
              .Append(l.ClassNameNormalized).Append(':').Append(l.TeacherUserId.ToString("N")).Append(':')
              .Append(l.RoomNormalized ?? string.Empty);
        }
        foreach (var u in mine.OrderBy(u => u.CycleDay).ThenBy(u => u.PeriodKey, StringComparer.Ordinal))
            sb.Append("|u").Append(u.CycleDay).Append(':').Append(u.PeriodKey ?? "*");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..24];
    }

    // =================================================================================================
    // The grid
    // =================================================================================================

    /// <summary>
    /// The cycle for one class and subject, as this caller may see it.
    ///
    /// The ORDER of the state tests is the disclosure rule. A slot the caller cannot use is reported
    /// with the LEAST revealing reason that is true: their own unavailability before the class being
    /// busy, the class being busy before somebody else holding it. A teacher learns why they cannot
    /// teach there and nothing about who else is.
    /// </summary>
    public static TimetableOpeningsDto BuildOpenings(
        Timetable timetable,
        TimetableSettingsDto settings,
        IReadOnlyList<TimetableLesson> lessons,
        Guid callerId,
        string className,
        Guid subjectId,
        string subjectName,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string> subjectNames,
        IReadOnlySet<string> rooms,
        bool showsNames,
        int? plannedPerWeek)
    {
        var cycleDays = TimetableCycle.CycleDayCount(settings);
        var classNorm = TimetableCycle.Normalize(className);
        var mine = settings.Unavailability.Where(u => u.UserId == callerId).ToList();

        var dto = new TimetableOpeningsDto
        {
            TimetableId = timetable.Id,
            TimetableName = timetable.Name,
            Status = timetable.Status,
            CycleDays = cycleDays,
            ClassName = className,
            SubjectId = subjectId,
            SubjectName = subjectName,
            ShowsNames = showsNames,
            PlannedPerWeek = plannedPerWeek,
            PlacedPerWeek = lessons.Count(l => l.TeacherUserId == callerId && l.ClassNameNormalized == classNorm && l.SubjectId == subjectId),
            Fingerprint = Fingerprint(timetable.Id, lessons, mine),
        };

        for (var day = 1; day <= cycleDays; day++)
        {
            var periods = TimetableCycle.LessonPeriodsOf(settings, cycleDays, day);
            var dayDto = new OpeningDayDto { CycleDay = day, Label = TimetableCycle.CycleDayLabel(settings, cycleDays, day) };

            foreach (var period in periods)
            {
                var here = lessons.Where(l => l.CycleDay == day && string.Equals(l.PeriodKey, period.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var slot = new OpeningSlotDto
                {
                    PeriodKey = period.Key,
                    Label = period.Label,
                    Start = period.Start,
                    End = period.End,
                };

                var mineHere = here.FirstOrDefault(l => l.TeacherUserId == callerId);
                var classHere = here.FirstOrDefault(l => l.ClassNameNormalized == classNorm);

                if (mineHere != null)
                {
                    slot.State = SlotState.Mine;
                    slot.MyLessonId = mineHere.Id;
                    slot.MyRoom = mineHere.Room;
                    if (showsNames)
                    {
                        slot.HeldClassName = mineHere.ClassName;
                        slot.HeldSubjectName = subjectNames.GetValueOrDefault(mineHere.SubjectId);
                    }
                }
                else if (mine.Any(u => u.CycleDay == day && (u.PeriodKey == null || string.Equals(u.PeriodKey, period.Key, StringComparison.OrdinalIgnoreCase))))
                {
                    // THE CALLER'S OWN DECLARATION COMES FIRST. They told us they cannot be here, so
                    // that is the true and least revealing answer — and it is one they can undo.
                    slot.State = SlotState.Unavailable;
                }
                else if (classHere != null)
                {
                    // The class is being taught something else. A teacher may know their class is
                    // busy; they still learn nothing about which colleague has it.
                    slot.State = SlotState.ClassBusy;
                    if (showsNames)
                    {
                        slot.HeldByName = names.GetValueOrDefault(classHere.TeacherUserId);
                        slot.HeldSubjectName = subjectNames.GetValueOrDefault(classHere.SubjectId);
                        slot.HeldClassName = classHere.ClassName;
                    }
                }
                else if (here.Count > 0 && here.All(l => l.TeacherUserId != callerId))
                {
                    // Somebody else is teaching in this period, but not this class — so it is only
                    // "taken" in the sense that the ROOM might be. The caller is free; the slot is not
                    // empty. Reported as Free, with the free rooms narrowed below.
                    slot.State = SlotState.Free;
                }
                else
                {
                    slot.State = SlotState.Free;
                }

                if (slot.State == SlotState.Free)
                {
                    var busyRooms = here.Where(l => l.RoomNormalized != null).Select(l => l.RoomNormalized!).ToHashSet();
                    slot.FreeRooms = rooms.Where(r => !busyRooms.Contains(TimetableCycle.Normalize(r))).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
                }

                dayDto.Slots.Add(slot);
            }

            dto.Days.Add(dayDto);
        }

        return dto;
    }

    // =================================================================================================
    // Whether a claim may proceed
    // =================================================================================================

    /// <summary>
    /// Everything that could refuse a claim, in the order a reader should hear it, with the request
    /// that would resolve it where one would. Returns null when the claim may go ahead.
    ///
    /// CALLED INSIDE THE TIMETABLE LOCK, against lessons re-read under that lock. Calling it against
    /// the set the client rendered from would be checking the past.
    /// </summary>
    public static (string Refusal, ConfigRequestKind? Offer)? RefuseClaim(
        Timetable timetable,
        TimetableSettingsDto settings,
        IReadOnlyList<TimetableLesson> lessons,
        Guid callerId,
        ClaimSlotRequest request,
        string classNorm,
        int? plannedPerWeek,
        bool capByPlannedLoad)
    {
        if (timetable.Status != TimetableStatus.Draft)
            return ("That timetable has been published, so it cannot be changed here. Ask for the change instead.", ConfigRequestKind.TimetableSlot);

        var cycleDays = TimetableCycle.CycleDayCount(settings);
        if (request.CycleDay < 1 || request.CycleDay > cycleDays)
            return ($"The cycle has {cycleDays} days.", null);

        var period = TimetableCycle.LessonPeriodOn(settings, cycleDays, request.CycleDay, request.PeriodKey);
        if (period == null)
            return ($"{TimetableCycle.CycleDayLabel(settings, cycleDays, request.CycleDay)} has no teaching period '{request.PeriodKey}'.", null);

        if (settings.Unavailability.Any(u => u.UserId == callerId && u.CycleDay == request.CycleDay
                && (u.PeriodKey == null || string.Equals(u.PeriodKey, request.PeriodKey, StringComparison.OrdinalIgnoreCase))))
            return ("You have declared yourself unavailable then. Change that first if it is out of date.", null);

        var here = lessons.Where(l => l.CycleDay == request.CycleDay
                                      && string.Equals(l.PeriodKey, request.PeriodKey, StringComparison.OrdinalIgnoreCase)
                                      && l.Id != request.MoveFromLessonId).ToList();

        if (here.Any(l => l.TeacherUserId == callerId))
            return ("You already have a lesson in that period.", ConfigRequestKind.SlotSwap);

        // THE TEACHER MAY NOT CREATE A CLASH THE MASTER IS ALLOWED TO CREATE. AddLesson refuses only a
        // teacher clash and leaves class and room clashes to the publish gate, deliberately: a master
        // builds a draft, holds a clash for a minute and fixes it. A teacher placing their own lesson
        // has no such workflow and no way to see the consequence, so both are refused here — and both
        // are offered as a request, which is how the answer stops being "no" and becomes "ask".
        if (here.Any(l => l.ClassNameNormalized == classNorm))
            return ("That class is already being taught then.", ConfigRequestKind.SlotSwap);

        if (!string.IsNullOrWhiteSpace(request.Room))
        {
            var roomNorm = TimetableCycle.Normalize(request.Room);
            if (here.Any(l => l.RoomNormalized == roomNorm))
                return ($"{request.Room} is in use then. Choose another room, or leave it blank.", null);
        }

        if (capByPlannedLoad && plannedPerWeek is { } planned && request.MoveFromLessonId == null)
        {
            var placed = lessons.Count(l => l.TeacherUserId == callerId && l.ClassNameNormalized == classNorm && l.SubjectId == request.SubjectId);
            if (placed >= planned)
                return ($"That is all {planned} period(s) a week this class is planned for. Ask if it needs more.", ConfigRequestKind.ClassAssignment);
        }

        return null;
    }

    // =================================================================================================
    // Declarations
    // =================================================================================================

    /// <summary>
    /// Replace the caller's own unavailability and preferences in the settings blob, LEAVING EVERY
    /// OTHER PERSON'S LINES EXACTLY AS THEY WERE.
    ///
    /// This is the whole safety of the write: the caller sends lines with no user id on them, and this
    /// stamps their own id on every one. A line for somebody else cannot be expressed, so it cannot be
    /// smuggled. Lines the timetable MASTER entered for this person are also preserved — only the
    /// person's own self-declared lines are replaced — because a teacher clearing their declarations
    /// must not quietly undo a constraint the school put on them.
    /// </summary>
    public static string? ApplyDeclarations(TimetableSettingsDto settings, Guid callerId, UpdateMyTeachingDeclarationsRequest request, int maxLines)
    {
        if (request.Unavailability != null)
        {
            if (request.Unavailability.Count > maxLines)
                return $"That is more than {maxLines} unavailability lines. Talk to whoever builds the timetable instead.";

            settings.Unavailability.RemoveAll(u => u.UserId == callerId && u.DeclaredBySelf);

            var now = DateTime.UtcNow;
            foreach (var line in request.Unavailability)
            {
                settings.Unavailability.Add(new TeacherUnavailabilityDto
                {
                    UserId = callerId,
                    CycleDay = line.CycleDay,
                    PeriodKey = string.IsNullOrWhiteSpace(line.PeriodKey) ? null : line.PeriodKey.Trim(),
                    Reason = line.Reason,
                    Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim(),
                    DeclaredBySelf = true,
                    DeclaredAt = now,
                });
            }
        }

        if (request.Preferences != null)
        {
            settings.Preferences.RemoveAll(p => p.UserId == callerId);
            settings.Preferences.Add(new StaffTeachingPreferenceDto
            {
                UserId = callerId,
                MaxConsecutivePeriods = request.Preferences.MaxConsecutivePeriods,
                PreferredLightCycleDay = request.Preferences.PreferredLightCycleDay,
                PreferredRooms = request.Preferences.PreferredRooms ?? new(),
                UpdatedAt = DateTime.UtcNow,
            });
        }

        return null;
    }

    // =================================================================================================
    // Requests
    // =================================================================================================

    /// <summary>
    /// A sentence a decider can read without opening anything. It names the SHAPE of what is asked and
    /// never a value that would be a disclosure on its own — the same rule the staff activity log
    /// follows when it names the field that moved rather than what it moved to.
    /// </summary>
    public static string SummaryOf(StaffConfigRequest r, string requesterName, string? subjectName, string? dayLabel) => r.Kind switch
    {
        ConfigRequestKind.TimetableSlot =>
            $"{requesterName} asks to teach {r.ClassName} {subjectName} at {dayLabel} {r.PeriodKey}.",
        // A swap says WHICH KIND it is, because the two are different decisions: one changes the timetable for
        // the rest of term and the other changes one afternoon.
        ConfigRequestKind.SlotSwap when r.EffectiveOn is { } once =>
            $"{requesterName} asks to swap a lesson at {dayLabel} {r.PeriodKey} on {Day(once)} only.",
        ConfigRequestKind.SlotSwap =>
            $"{requesterName} asks to swap a lesson at {dayLabel} {r.PeriodKey} for the rest of the term.",
        ConfigRequestKind.LessonCover =>
            $"{requesterName} asks a colleague to cover {r.ClassName} {subjectName} at {dayLabel} {r.PeriodKey}" +
            (r.EffectiveOn is { } on ? $" on {Day(on)}." : "."),
        ConfigRequestKind.ClassAssignment =>
            $"{requesterName} asks to teach {subjectName} to {r.ClassName}" +
            (r.PeriodsPerWeek is { } n ? $", {n} period(s) a week." : "."),
        _ => $"{requesterName} has made a request.",
    };

    /// <summary>Invariant, because the dev machine is en-GB and the server is not — the standing rule for API-built text.</summary>
    private static string Day(DateOnly d) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{d:dd MMM yyyy}");

    /// <summary>
    /// Why this request cannot be decided by this person. Null means they may.
    ///
    /// THE SELF-APPROVAL REFUSAL IS THE POINT AND IT IS CHECKED HERE, not at the permission layer. A
    /// Director of Studies holds timetable.manage and also teaches, so the permission says yes on
    /// their own request; this is what says no. NIST SP 800-53 AC-5, and the same rule as "nobody
    /// marks their own register entry" and "a meeting cannot adopt its own minutes".
    /// </summary>
    public static string? RefuseDecision(StaffConfigRequest request, Guid deciderId, bool mayDecide)
    {
        if (!mayDecide)
            return "Deciding a request needs the timetable permission, or being the appointed master of the timetable it is about.";

        if (request.RequestedByUserId == deciderId)
            return "You cannot decide your own request. Somebody else has to.";

        // G6 (2026-09-26): the colleague in a swap or cover agrees; somebody else decides. One person doing both is two
        // of the three people in the flow.
        if (DutySeparation.Refusal(deciderId, null, "decide a request you are part of", request.CounterpartUserId, request.CoverUserId) is { } party)
            return party;

        if (request.State != ConfigRequestState.Pending)
            return "That request has already been decided.";

        if (NeedsCounterpart(request.Kind) && request.CounterpartAgreedAt == null)
            return request.Kind == ConfigRequestKind.LessonCover
                ? "The colleague has not agreed to cover it yet."
                : "The other teacher has not agreed to the swap yet.";

        return null;
    }

    /// <summary>
    /// The kinds that cannot be decided until the OTHER teacher has said yes. A decider moving somebody's lesson
    /// without their agreement is exactly the corridor negotiation this feature exists to replace rather than to
    /// automate — so cover is on this list for the same reason a swap is.
    /// </summary>
    public static bool NeedsCounterpart(ConfigRequestKind kind)
        => kind is ConfigRequestKind.SlotSwap or ConfigRequestKind.LessonCover;

    /// <summary>
    /// True when approving this writes a dated exception rather than re-publishing the timetable.
    ///
    /// The difference matters to the DECIDER, not only to the code: a one-off cannot disturb a class, a room or a
    /// cohort's day, because nothing moves — only who stands in front of it. A permanent swap does move lessons,
    /// so it runs TimetableChecker and its warnings are what the decider is actually being asked about.
    /// </summary>
    public static bool IsOneOff(StaffConfigRequest r)
        => r.Kind == ConfigRequestKind.LessonCover || (r.Kind == ConfigRequestKind.SlotSwap && r.EffectiveOn != null);
}
