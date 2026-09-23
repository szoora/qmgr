using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.API.Controllers.v1;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// CHANGING A PUBLISHED TIMETABLE WITHOUT BREAKING ITS IMMUTABILITY (2026-09-22).
///
/// A published version is never edited — "a change to it is a new draft published over it" is this module's own
/// rule, and it is what lets an archived version stand as the record of what the timetable said before. So a
/// PERMANENT swap agreed between two teachers mid-term cannot be an UPDATE: it is a copy, a trade, a check and a
/// publish, which is exactly what a timetable master would do by hand.
///
/// This exists because the self-service swap COULD NOT DO IT AT ALL until now. Its apply path called
/// <c>CurrentDraftAsync</c>, so a mid-term swap — which is when two teachers actually have this conversation,
/// because before term starts the master is still building the draft — was accepted, agreed by the colleague,
/// approved by a decider, and then refused with "there is no draft timetable to change any more". Three people
/// acted and the system refused last.
///
/// TWO THINGS HERE ARE LOAD-BEARING:
///
/// THE DATE RANGE IS KEPT, NOT SPLIT AT THE SWAP DATE. Cycle day 1 is anchored on <c>EffectiveFrom</c>
/// (<see cref="TimetableCycle.CycleDayOn"/>), so moving it forward would shift every cycle day of an A/B
/// timetable — a two-week cycle would silently invert. A school that genuinely needs "this arrangement from the
/// 10th" is asking for a dated split, which is a different feature and its own decision.
///
/// THE CHECKER RUNS, AND THIS IS THE ONLY PATH THAT CAN. Two teachers can agree to trade Tuesday P3 for Thursday
/// P5 and leave a cohort with double Maths and no Physics that week, or move a practical off its lab slot. Neither
/// of them can see that; <see cref="TimetableChecker"/> can. Hard issues refuse; soft issues are returned so the
/// decider reads them BEFORE pressing approve, which is where a decider earns their place in a swap — not to
/// second-guess the two teachers, but to see what the two teachers cannot.
/// </summary>
public interface ITimetableRepublishService
{
    /// <summary>
    /// What a permanent swap would do, without writing anything. Fills the decider's warnings. Never throws:
    /// a preview that fails is shown as "could not be checked", not as a refusal.
    /// </summary>
    Task<SwapPreview> PreviewSwapAsync(Guid timetableId, Guid myLessonId, Guid theirLessonId, CancellationToken ct = default);

    /// <summary>
    /// Copy the published version into a new draft with the two slots traded, then publish it over the original.
    /// <c>Refusal</c> is non-null when nothing was written.
    /// </summary>
    Task<SwapResult> SwapAndRepublishAsync(Guid timetableId, Guid myLessonId, Guid theirLessonId, Guid actorId, string reason, CancellationToken ct = default);
}

/// <summary>What a swap would cost, in the decider's terms. <see cref="Blocked"/> non-null means it cannot be applied at all.</summary>
public sealed record SwapPreview(string? Blocked, List<string> Warnings);

public sealed record SwapResult(string? Refusal, Guid? NewTimetableId, Guid? MyNewLessonId);

public class TimetableRepublishService : ITimetableRepublishService
{
    private readonly QMgrDbContext _db;
    private readonly ITimetableSettingsService _settings;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ILogger<TimetableRepublishService> _logger;

    public TimetableRepublishService(QMgrDbContext db, ITimetableSettingsService settings,
        IStaffPerformancePolicyService policy, ILogger<TimetableRepublishService> logger)
    {
        _db = db;
        _settings = settings;
        _policy = policy;
        _logger = logger;
    }

    public async Task<SwapPreview> PreviewSwapAsync(Guid timetableId, Guid myLessonId, Guid theirLessonId, CancellationToken ct = default)
    {
        try
        {
            var timetable = await _db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == timetableId, ct);
            if (timetable == null) return new SwapPreview("That timetable no longer exists.", new());

            var lessons = await _db.TimetableLessons.AsNoTracking().Where(l => l.TimetableId == timetableId).ToListAsync(ct);
            var mine = lessons.FirstOrDefault(l => l.Id == myLessonId);
            var theirs = lessons.FirstOrDefault(l => l.Id == theirLessonId);
            if (mine == null || theirs == null) return new SwapPreview("One of those lessons has already been changed.", new());

            // The trade, in memory only. Diagnosing a COPY is what makes this a preview rather than a dry run with
            // a rollback — nothing is tracked, so nothing can leak into a save somewhere else in the request.
            var trial = lessons.Select(Clone).ToList();
            Trade(trial, mine.Id, theirs.Id);

            var diagnosis = await DiagnoseAsync(timetable, trial, ct);
            var before = await DiagnoseAsync(timetable, lessons.Select(Clone).ToList(), ct);

            if (diagnosis.HardCount > 0)
                return new SwapPreview(
                    $"That swap would create {diagnosis.HardCount} clash(es) that cannot be published: " +
                    string.Join("; ", diagnosis.Issues.Where(i => i.Severity == TimetableIssueSeverity.Hard).Take(3).Select(i => i.Message)),
                    new());

            // ONLY WHAT THE SWAP ITSELF INTRODUCES. A published timetable routinely carries acknowledged soft
            // issues, and listing those back would make every swap look as though it broke something.
            var existing = before.Issues.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = diagnosis.Issues.Where(i => !existing.Contains(i.Key)).Select(i => i.Message).Distinct().Take(6).ToList();
            return new SwapPreview(null, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not preview the swap on timetable {TimetableId}", timetableId);
            return new SwapPreview(null, new List<string> { "The effect on other classes could not be checked. Look at the timetable before approving." });
        }
    }

    public async Task<SwapResult> SwapAndRepublishAsync(Guid timetableId, Guid myLessonId, Guid theirLessonId, Guid actorId, string reason, CancellationToken ct = default)
    {
        SwapResult result = new("That swap could not be applied.", null, null);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var source = await _db.Timetables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == timetableId, ct);
            if (source == null) { result = new("That timetable no longer exists.", null, null); return; }

            // The SAME two locks publishing itself takes, in the same order. A swap is a publish, so it cannot be
            // allowed to race one: two of these at once would each copy the other's "before" and one trade would
            // be lost with nothing anywhere saying so.
            var branchLock = $"timetable-publish:{source.BranchId}";
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({branchLock})::bigint)", ct);
            var ttLock = $"timetable:{timetableId}";
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({ttLock})::bigint)", ct);

            // Re-read INSIDE the locks: the version may have been replaced while we waited.
            var timetable = await _db.Timetables.FirstOrDefaultAsync(t => t.Id == timetableId, ct);
            if (timetable == null) { result = new("That timetable no longer exists.", null, null); return; }
            if (timetable.Status != TimetableStatus.Published)
            {
                result = new("That timetable is no longer the published one, so the swap was not applied.", null, null);
                return;
            }

            var lessons = await _db.TimetableLessons.AsNoTracking().Where(l => l.TimetableId == timetableId).ToListAsync(ct);
            if (lessons.FirstOrDefault(l => l.Id == myLessonId) is not { } mine || lessons.FirstOrDefault(l => l.Id == theirLessonId) is not { } theirs)
            {
                result = new("One of those lessons has already been changed.", null, null);
                return;
            }

            // The new version: same name, same dates, same cycle, same appointed masters. Same name because it IS
            // the same timetable — the list distinguishes them by status and published date, and renaming it
            // "Term 3 (v2)" would make a school's own timetable look like a working file.
            var replacement = new Timetable
            {
                OrganizationId = timetable.OrganizationId,
                BranchId = timetable.BranchId,
                Name = timetable.Name,
                PeriodKey = timetable.PeriodKey,
                CycleDays = timetable.CycleDays,
                EffectiveFrom = timetable.EffectiveFrom,
                EffectiveTo = timetable.EffectiveTo,
                ManagerUserIds = timetable.ManagerUserIds,
                Status = TimetableStatus.Draft,
                CreatedBy = actorId
            };

            // Group ids are re-minted so a joint lesson in the copy is its own group, exactly as CreateTimetable's
            // copy path does. The map keeps a joint lesson joint rather than splitting it into singles.
            var groups = new Dictionary<Guid, Guid>();
            var copyOf = new Dictionary<Guid, TimetableLesson>();
            foreach (var l in lessons)
            {
                var copy = new TimetableLesson
                {
                    CycleDay = l.CycleDay, PeriodKey = l.PeriodKey, ClassName = l.ClassName, ClassNameNormalized = l.ClassNameNormalized,
                    SubjectId = l.SubjectId, TeacherUserId = l.TeacherUserId, Room = l.Room, RoomNormalized = l.RoomNormalized,
                    GroupId = l.GroupId is { } g ? (groups.TryGetValue(g, out var ng) ? ng : groups[g] = Guid.NewGuid()) : null,
                    CreatedBy = actorId
                };
                replacement.Lessons.Add(copy);
                copyOf[l.Id] = copy;
            }

            // Trade the SLOTS on the copies, never the teachers. Each keeps their class and subject and changes
            // when they teach it; swapping teachers would move a class to somebody not assigned to it, which is a
            // data-access change wearing a timetable change's clothes.
            var a = copyOf[mine.Id];
            var b = copyOf[theirs.Id];
            (a.CycleDay, b.CycleDay) = (b.CycleDay, a.CycleDay);
            (a.PeriodKey, b.PeriodKey) = (b.PeriodKey, a.PeriodKey);

            var diagnosis = await DiagnoseAsync(replacement, replacement.Lessons.ToList(), ct);
            if (diagnosis.HardCount > 0)
            {
                result = new($"That swap would create {diagnosis.HardCount} clash(es) that cannot be published, so nothing was changed.", null, null);
                return;
            }

            _db.Timetables.Add(replacement);
            await _db.SaveChangesAsync(ct);

            // Archive FIRST and in its own statement: the one-published-per-start-date index refuses the pair in
            // either order otherwise. Publishing's own path does exactly this.
            timetable.Status = TimetableStatus.Archived;
            timetable.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            replacement.Status = TimetableStatus.Published;
            replacement.PublishedAt = DateTime.UtcNow;
            replacement.PublishedByUserId = actorId;
            replacement.ReportedIssueKeys = Array.Empty<string>();
            replacement.UpdatedAt = DateTime.UtcNow;

            // DATED EXCEPTIONS DIE WITH THE VERSION THEY WERE AGAINST — the foreign key cascades — and that is
            // correct rather than convenient: an exception names a lesson id, and every lesson in the replacement
            // is a new row. Carrying them across by matching teacher, slot and class would be a guess, and a
            // wrongly carried cover puts the wrong person in front of a class. Cover arranged for a date beyond
            // this swap has to be arranged again, and the people affected were told when it was withdrawn.
            var carriedOver = await _db.TimetableLessonExceptions
                .Where(e => e.TimetableId == timetableId && e.Date >= DateOnly.FromDateTime(DateTime.UtcNow))
                .CountAsync(ct);

            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                result = new("Another version was published at the same moment, so the swap was not applied.", null, null);
                return;
            }

            if (carriedOver > 0)
                _logger.LogInformation("Swap re-publish of timetable {TimetableId} dropped {Count} future cover arrangement(s) with the replaced version", timetableId, carriedOver);

            _logger.LogInformation("Timetable {TimetableId} re-published as {NewId} for a swap: {Reason}", timetableId, replacement.Id, reason);
            result = new(null, replacement.Id, a.Id);
        });

        if (result.Refusal == null && result.NewTimetableId != null)
        {
            var branchId = await _db.Timetables.AsNoTracking().Where(t => t.Id == result.NewTimetableId).Select(t => t.BranchId).FirstOrDefaultAsync(ct);
            if (branchId != Guid.Empty)
            {
                try { Hangfire.BackgroundJob.Enqueue<QMgr.Infrastructure.Jobs.LessonGenerationJob>(job => job.RunForBranchAsync(branchId)); }
                catch (Exception ex) { _logger.LogError(ex, "Could not enqueue lesson generation for branch {BranchId} after a swap", branchId); }
            }
        }

        return result;
    }

    private static TimetableLesson Clone(TimetableLesson l) => new()
    {
        Id = l.Id, TimetableId = l.TimetableId, CycleDay = l.CycleDay, PeriodKey = l.PeriodKey,
        ClassName = l.ClassName, ClassNameNormalized = l.ClassNameNormalized, SubjectId = l.SubjectId,
        TeacherUserId = l.TeacherUserId, Room = l.Room, RoomNormalized = l.RoomNormalized, GroupId = l.GroupId
    };

    private static void Trade(List<TimetableLesson> lessons, Guid aId, Guid bId)
    {
        var a = lessons.First(l => l.Id == aId);
        var b = lessons.First(l => l.Id == bId);
        (a.CycleDay, b.CycleDay) = (b.CycleDay, a.CycleDay);
        (a.PeriodKey, b.PeriodKey) = (b.PeriodKey, a.PeriodKey);
    }

    private async Task<TimetableDiagnosisDto> DiagnoseAsync(Timetable timetable, List<TimetableLesson> lessons, CancellationToken ct)
    {
        var settings = await _settings.ReadAsync(timetable.BranchId, ct);
        var policy = await _policy.GetAsync(timetable.OrganizationId);
        var zone = AppointmentScheduling.ResolveTimeZone(
            await _db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == timetable.BranchId).Select(b => b.Timezone).FirstOrDefaultAsync(ct));
        var ctx = await TimetableChecker.LoadContextAsync(_db, timetable, settings, policy, zone, lessons);
        return TimetableChecker.Diagnose(timetable, lessons, ctx);
    }
}
