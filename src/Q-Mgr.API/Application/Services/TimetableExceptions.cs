using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// ONE-DAY DEPARTURES FROM THE PUBLISHED TIMETABLE (2026-09-22): cover, and cancellation. The one home for
/// reading them, for the cycle arithmetic a one-off swap needs, and for the sentence a person reads.
///
/// Called by <c>StaffLessons.MaterialiseAsync</c> (which is the ONLY thing that acts on them — an exception
/// does nothing until a lesson duty is generated from it), by TimetableController's own cover endpoints, and by
/// the self-service approval path when a one-off swap is applied.
///
/// A ONE-OFF SWAP IS TWO COVERS, and that is the design rather than an implementation shortcut: a one-off does
/// not change WHEN a class is taught, only WHO teaches it, so a class, a room and a cohort's day are never
/// disturbed. Moving a lesson to a different slot for one day IS a timetable change and goes through a
/// re-publish, where TimetableChecker can see the class-side effect.
/// </summary>
public static class TimetableExceptions
{
    /// <summary>
    /// The exceptions in force for a branch over a date range, keyed for the materialiser. Only the versions
    /// named are read: an exception against a replaced version means nothing.
    /// </summary>
    public static async Task<Dictionary<(Guid LessonId, DateOnly Date), TimetableLessonException>> ReadAsync(
        QMgrDbContext db, Guid branchId, IReadOnlyCollection<Guid> timetableIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (timetableIds.Count == 0) return new();
        var rows = await db.TimetableLessonExceptions.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.BranchId == branchId && timetableIds.Contains(e.TimetableId) && e.Date >= from && e.Date <= to)
            .ToListAsync(ct);
        // Last write wins on a duplicate, which the unique index makes impossible anyway; the grouping is
        // belt and braces so a stray pair can never throw here and stop every lesson in the school being made.
        return rows.GroupBy(e => (e.TimetableLessonId, e.Date))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).First());
    }

    /// <summary>
    /// The date in the SAME cycle week as <paramref name="anchorDate"/> that falls on
    /// <paramref name="wantedCycleDay"/>, or null when the cycle has no such day.
    ///
    /// This is what lets a one-off swap carry ONE date rather than two that could disagree: the asker names the
    /// date of their own lesson, and the colleague's date is derived. The arithmetic is not reimplemented —
    /// every candidate is put back through <see cref="TimetableCycle.CycleDayOn"/>, which stays the one source
    /// of truth for what cycle day a date is.
    /// </summary>
    public static DateOnly? SameCycleDateFor(TimetableSettingsDto settings, int cycleDays, DateOnly effectiveFrom, DateOnly anchorDate, int wantedCycleDay)
    {
        var days = TimetableCycle.TeachingDays(settings);
        if (days.Count == 0) return null;
        var weeks = Math.Max(1, cycleDays / days.Count);

        var monday = anchorDate.AddDays(-(((int)anchorDate.DayOfWeek + 6) % 7));
        var anchorMonday = effectiveFrom.AddDays(-(((int)effectiveFrom.DayOfWeek + 6) % 7));
        var weekNumber = (int)Math.Floor((monday.DayNumber - anchorMonday.DayNumber) / 7.0);
        var weekInCycle = ((weekNumber % weeks) + weeks) % weeks;
        var cycleStart = monday.AddDays(-7 * weekInCycle);

        for (var i = 0; i < 7 * weeks; i++)
        {
            var candidate = cycleStart.AddDays(i);
            if (TimetableCycle.CycleDayOn(settings, cycleDays, effectiveFrom, candidate) == wantedCycleDay) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Why this cover or cancellation cannot be recorded, or null when it may. Called by BOTH write paths —
    /// the master's own endpoint and the self-service approval — so a request the preview accepted cannot be
    /// refused in different words later.
    /// </summary>
    public static string? Refuse(Timetable timetable, TimetableLesson lesson, DateOnly date, DateOnly today,
        LessonExceptionKind kind, Guid? coverUserId, TimetableSettingsDto settings)
    {
        if (timetable.Status != TimetableStatus.Published)
            return "Cover is recorded against a published timetable. A draft is changed by editing it.";

        if (date < today)
            return "That date has passed. Cover is arranged before the lesson, not recorded after it — mark the register instead.";

        if (date < timetable.EffectiveFrom || date > timetable.EffectiveTo)
            return $"{Day(date)} is outside this timetable, which runs {Day(timetable.EffectiveFrom)} to {Day(timetable.EffectiveTo)}.";

        // The lesson has to actually fall on that date, or the exception describes nothing. This is the check
        // that stops "cover my Tuesday lesson on Thursday" being stored and then silently doing nothing.
        var cycleDay = TimetableCycle.CycleDayOn(settings, timetable.CycleDays, timetable.EffectiveFrom, date);
        if (cycleDay == null)
            return $"{Day(date)} is not a teaching day in the bell schedule.";
        if (cycleDay != lesson.CycleDay)
            return $"That lesson is not taught on {Day(date)} — it falls on {TimetableCycle.CycleDayLabel(settings, timetable.CycleDays, lesson.CycleDay)}.";

        if (kind == LessonExceptionKind.Cover)
        {
            if (coverUserId is null || coverUserId == Guid.Empty)
                return "Name the colleague who is covering it.";
            if (coverUserId == lesson.TeacherUserId)
                return "That is already their lesson.";
        }
        else if (coverUserId != null)
        {
            return "A cancelled lesson has nobody covering it.";
        }

        return null;
    }

    /// <summary>
    /// A sentence a person reads without opening anything. It names the class and the period, never anything
    /// about a child, and the REASON is included because a cover with no reason reads as an instruction.
    /// </summary>
    public static string SummaryOf(TimetableLessonException row, string teacherName, string? coverName, string className, string? subjectName, string periodLabel)
    {
        var what = string.IsNullOrWhiteSpace(subjectName) ? className : $"{className} {subjectName}";
        var when = Day(row.Date);
        return row.Kind switch
        {
            LessonExceptionKind.Cover =>
                $"{coverName ?? "A colleague"} covers {teacherName}'s {what} on {when}, {periodLabel}.",
            _ => $"{teacherName}'s {what} does not happen on {when}.",
        };
    }

    /// <summary>
    /// Dates in server-built text are INVARIANT. The dev machine is en-GB and the server is not, so a bare
    /// <c>{x:dd MMM}</c> reads "Sept" in one place and "Sep" in the other — a standing rule in this module.
    /// </summary>
    private static string Day(DateOnly d) => string.Create(CultureInfo.InvariantCulture, $"{d:dd MMM yyyy}");
}
