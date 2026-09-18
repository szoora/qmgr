using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.API.Controllers.v1;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

/// <summary>
/// Lessons in the day (duty rota plan §7): materialising the published timetable into Lesson duties, deriving where each
/// lesson stands, and writing a lesson's flag. One home, so the controller, the nightly job, My Day and the reminder
/// sweep agree on what "unrecorded", "missed" and "not recovered" mean.
///
/// A lesson is a StaffDuty of kind Lesson: the teacher is its only expected person and its only named recorder. Lesson
/// supervisors (holders of <c>timetable.lessons.flag</c> whose staff scope covers the teacher) are decided per request,
/// never stored on the row, so a head who leaves a department stops seeing its lessons on the next request.
/// </summary>
public static class StaffLessons
{
    public const string AttendanceParameter = "Lesson Attendance";
    public const string RecoveryParameter = "Lesson Recovery";

    /// <summary>How far ahead lessons are materialised (plan §7.1).</summary>
    public const int WindowDays = 14;

    // ---- Parameters -------------------------------------------------------------------------------------------

    /// <summary>The Lesson Attendance and Lesson Recovery parameters, inserted by name from the defaults when the tenant has neither.</summary>
    public static async Task<(Guid Attendance, Guid Recovery)> EnsureParametersAsync(QMgrDbContext db, IStaffPerformancePolicyService policy, Guid organizationId, ILogger logger)
    {
        var attendance = await EnsureAsync(db, policy, organizationId, AttendanceParameter, null, logger);
        var recovery = await EnsureAsync(db, policy, organizationId, RecoveryParameter, attendance, logger);
        return (attendance, recovery);
    }

    private static async Task<Guid> EnsureAsync(QMgrDbContext db, IStaffPerformancePolicyService policy, Guid organizationId, string name, Guid? offsets, ILogger logger)
    {
        var existing = await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.Name.ToLower() == name.ToLower())
            .OrderByDescending(p => p.IsActive).Select(p => (Guid?)p.Id).FirstOrDefaultAsync();
        if (existing != null) return existing.Value;

        var definition = policy.DefaultParameters().First(p => p.Name == name);
        var maxSort = await db.PerformanceParameters.IgnoreQueryFilters().Where(p => p.OrganizationId == organizationId).MaxAsync(p => (int?)p.SortOrder) ?? 0;
        var parameter = new PerformanceParameter { OrganizationId = organizationId };
        StaffPerformanceMapping.Apply(parameter, definition with { SortOrder = maxSort + 1 });
        if (offsets != null) parameter.OffsetsParameterId = offsets;
        db.PerformanceParameters.Add(parameter);
        try
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Added the {Parameter} parameter for organization {OrganizationId}", name, organizationId);
            return parameter.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(parameter).State = EntityState.Detached;
            return await db.PerformanceParameters.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && p.Name.ToLower() == name.ToLower()).Select(p => p.Id).FirstAsync();
        }
    }

    // ---- Materialisation (plan §7.1) --------------------------------------------------------------------------

    public sealed record MaterialiseResult(int Created, int Cancelled, int Changed);

    /// <summary>
    /// Writes the next <see cref="WindowDays"/> days of Lesson duties for a branch from the published version in force on
    /// each date, skipping days outside every term. Idempotent: the unique (TimetableLessonId, StartsAt) index plus a
    /// per-branch advisory lock make an overlap of the nightly run and a publish's run a no-op.
    ///
    /// A future lesson that no longer appears — the version was replaced — is cancelled, never deleted, and only while
    /// nobody has flagged it. When today's lesson moves or disappears after the teacher could have read the morning
    /// digest, they are told (plan §7.2 "Changes").
    /// </summary>
    public static async Task<MaterialiseResult> MaterialiseAsync(QMgrDbContext db, ITimetableSettingsService settingsService, IStaffPerformancePolicyService policyService,
        INotificationService notifications, ILogger logger, Guid branchId, DateTime nowUtc, CancellationToken ct = default)
    {
        var branch = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == branchId).Select(b => new { b.Id, b.OrganizationId, b.Timezone }).FirstOrDefaultAsync(ct);
        if (branch == null) return new MaterialiseResult(0, 0, 0);
        var zone = AppointmentScheduling.ResolveTimeZone(branch.Timezone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var last = today.AddDays(WindowDays - 1);
        var settings = await settingsService.ReadAsync(branchId, ct);
        var policy = await policyService.GetAsync(branch.OrganizationId);

        var versions = await db.Timetables.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.BranchId == branchId && t.Status == TimetableStatus.Published && t.EffectiveFrom <= last && t.EffectiveTo >= today)
            .OrderByDescending(t => t.PublishedAt).ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var lessons = versionIds.Count == 0 ? new List<TimetableLesson>() : await db.TimetableLessons.IgnoreQueryFilters().AsNoTracking()
            .Where(l => versionIds.Contains(l.TimetableId)).ToListAsync(ct);
        var subjects = await db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == branch.OrganizationId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var (attendanceId, _) = await EnsureParametersAsync(db, policyService, branch.OrganizationId, logger);

        // What should exist: one duty per teacher per lesson (a joint lesson is one duty naming every class).
        var desired = new List<StaffDuty>();
        for (var date = today; date <= last; date = date.AddDays(1))
        {
            if (StaffRota.IsHoliday(policy, date)) continue;
            var version = versions.FirstOrDefault(v => v.EffectiveFrom <= date && v.EffectiveTo >= date);
            if (version == null) continue;
            if (TimetableCycle.CycleDayOn(settings, version.CycleDays, version.EffectiveFrom, date) is not { } cycleDay) continue;
            foreach (var group in lessons.Where(l => l.TimetableId == version.Id && l.CycleDay == cycleDay).GroupBy(l => (l.GroupId ?? l.Id, l.TeacherUserId)))
            {
                var rows = group.OrderBy(r => r.Id).ToList();
                var first = rows[0];
                if (TimetableCycle.LessonPeriodOn(settings, version.CycleDays, cycleDay, first.PeriodKey) is not { } period) continue;
                var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimetableCycle.ParseTime(period.Start)!.Value), zone);
                var end = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimetableCycle.ParseTime(period.End)!.Value), zone);
                if (end <= nowUtc) continue;
                var className = string.Join(" + ", rows.Select(r => r.ClassName).Distinct(StringComparer.OrdinalIgnoreCase));
                var subjectName = subjects.GetValueOrDefault(first.SubjectId, "Lesson");
                desired.Add(new StaffDuty
                {
                    OrganizationId = branch.OrganizationId, BranchId = branchId, ParameterId = attendanceId, Kind = DutyKind.Lesson,
                    Title = Truncate($"{className} {subjectName}", 200), Location = first.Room, StartsAt = start, EndsAt = end,
                    ExpectedUserIds = new[] { first.TeacherUserId }, RecorderUserIds = new[] { first.TeacherUserId },
                    TimetableLessonId = first.Id, ClassName = Truncate(className, 100), SubjectId = first.SubjectId, Room = first.Room,
                    CreatedByUserId = version.PublishedByUserId ?? Guid.Empty, CreatedBy = version.PublishedByUserId
                });
            }
        }

        var windowStart = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
        var windowEnd = TimeZoneInfo.ConvertTimeToUtc(last.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var created = 0;
        var cancelledDuties = new List<StaffDuty>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            created = 0;
            cancelledDuties = new();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var lockKey = $"lesson-generation:{branchId}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);

            var existing = await db.StaffDuties.IgnoreQueryFilters()
                .Where(d => d.BranchId == branchId && d.Kind == DutyKind.Lesson && d.TimetableLessonId != null && d.StartsAt >= windowStart && d.StartsAt < windowEnd)
                .ToListAsync(ct);
            var existingKeys = existing.Select(d => (d.TimetableLessonId!.Value, d.StartsAt)).ToHashSet();
            var pending = desired.Where(d => !existingKeys.Contains((d.TimetableLessonId!.Value, d.StartsAt))).ToList();

            // A re-published version is a copy with new lesson ids. A lesson that is the same in it — same teacher, time,
            // class and subject — keeps its row (its reminder stage, its register) and is only re-pointed at the new
            // lesson, rather than cancelled and made again, which would tell the teacher it "moved from 10:20 to 10:20".
            var desiredIds = desired.Select(d => (d.TimetableLessonId!.Value, d.StartsAt)).ToHashSet();
            // A lesson cancelled by hand (a school event) stays cancelled through a re-publish; one cancelled only because an
            // earlier version dropped it is made again if a later version brings it back.
            foreach (var old in existing.Where(d => (d.IsActive || (d.Description ?? "").StartsWith("Cancelled:", StringComparison.Ordinal))
                                                    && !desiredIds.Contains((d.TimetableLessonId!.Value, d.StartsAt))).ToList())
            {
                var same = pending.FirstOrDefault(n => n.ExpectedUserIds![0] == old.ExpectedUserIds?.FirstOrDefault() && n.StartsAt == old.StartsAt && n.EndsAt == old.EndsAt
                                                      && n.SubjectId == old.SubjectId && string.Equals(n.ClassName, old.ClassName, StringComparison.OrdinalIgnoreCase));
                if (same == null) continue;
                old.TimetableLessonId = same.TimetableLessonId;
                old.Room = same.Room;
                old.Location = same.Room;
                old.Title = same.Title;
                old.UpdatedAt = nowUtc;
                pending.Remove(same);
            }

            foreach (var d in pending)
            {
                db.StaffDuties.Add(d);
                created++;
            }

            var desiredKeys = desired.Select(d => (d.TimetableLessonId!.Value, d.StartsAt)).ToHashSet();
            var stale = existing.Where(d => d.IsActive && d.StartsAt > nowUtc && !desiredKeys.Contains((d.TimetableLessonId!.Value, d.StartsAt))).ToList();
            if (stale.Count > 0)
            {
                var staleIds = stale.Select(d => d.Id).ToList();
                var flagged = await db.StaffPerformanceRecords.IgnoreQueryFilters().Where(r => r.DutyId != null && staleIds.Contains(r.DutyId.Value)).Select(r => r.DutyId!.Value).Distinct().ToListAsync(ct);
                foreach (var d in stale.Where(d => !flagged.Contains(d.Id)))
                {
                    d.IsActive = false;
                    d.Description = "No longer on the published timetable.";
                    d.UpdatedAt = nowUtc;
                    cancelledDuties.Add(d);
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        // Changes to TODAY's lessons after the digest hour: one bell item per teacher per lesson (plan §7.2).
        var changed = 0;
        var digestTime = TimetableCycle.ParseTime(policy.MyDayLocalTime) ?? new TimeOnly(6, 30);
        var digestPassed = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone)) >= digestTime;
        if (digestPassed)
        {
            foreach (var gone in cancelledDuties.Where(d => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(d.StartsAt, zone)) == today))
            {
                var teacher = gone.ExpectedUserIds?.FirstOrDefault() ?? Guid.Empty;
                var replacement = desired.FirstOrDefault(n => n.ExpectedUserIds![0] == teacher && n.SubjectId == gone.SubjectId && string.Equals(n.ClassName, gone.ClassName, StringComparison.OrdinalIgnoreCase)
                                                             && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(n.StartsAt, zone)) == today);
                var at = TimeZoneInfo.ConvertTimeFromUtc(gone.StartsAt, zone);
                var message = replacement == null
                    ? string.Create(CultureInfo.InvariantCulture, $"{gone.Title} at {at:HH:mm} is no longer on today's timetable.")
                    : string.Create(CultureInfo.InvariantCulture, $"{gone.Title} moved from {at:HH:mm} to {TimeZoneInfo.ConvertTimeFromUtc(replacement.StartsAt, zone):HH:mm}{(string.IsNullOrEmpty(replacement.Room) ? "" : $" in {replacement.Room}")}.");
                try
                {
                    await notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                    {
                        UserId = teacher, OrganizationId = gone.OrganizationId, BranchId = branchId,
                        Title = replacement == null ? "A lesson today was removed" : "A lesson today moved",
                        Message = message, Type = NotificationType.StaffPerformance, Priority = NotificationPriority.Normal,
                        Channels = NotificationChannel.InApp, EventKey = NotificationEventKeys.StaffLessonChanged, ActionUrl = "/my-day", IconClass = "arrow-left-right"
                    });
                    changed++;
                }
                catch (Exception ex) { logger.LogError(ex, "Lesson change notice for duty {DutyId} failed", gone.Id); }
            }
        }

        if (created > 0 || cancelledDuties.Count > 0)
            logger.LogInformation("Lessons for branch {BranchId}: {Created} created, {Cancelled} cancelled, {Changed} teacher(s) told of a change today", branchId, created, cancelledDuties.Count, changed);
        return new MaterialiseResult(created, cancelledDuties.Count, changed);
    }

    // ---- Status (plan §7.3) -----------------------------------------------------------------------------------

    public static bool IsTaught(DutyOutcome o) => o is DutyOutcome.Present or DutyOutcome.Late or DutyOutcome.Recovered or DutyOutcome.Completed;
    public static bool IsMissed(DutyOutcome o) => o is DutyOutcome.Absent or DutyOutcome.Excused;

    public static LessonStatus StatusOf(StaffDuty lesson, StaffPerformanceRecord? record, StaffDuty? recovery, StaffPerformanceRecord? recoveryRecord, DateTime nowUtc, int recoveryDeadlineDays)
    {
        if (!lesson.IsActive) return LessonStatus.Cancelled;
        if (record == null) return nowUtc < lesson.StartsAt ? LessonStatus.Scheduled : LessonStatus.Unrecorded;
        if (IsTaught(record.Outcome)) return record.Source == RecordSource.SelfReport ? LessonStatus.TaughtSelfReported : LessonStatus.Taught;
        if (!IsMissed(record.Outcome)) return LessonStatus.Unrecorded;

        if (recovery != null && recoveryRecord != null && IsTaught(recoveryRecord.Outcome)) return LessonStatus.Recovered;
        if (recovery != null && recovery.IsActive) return LessonStatus.RecoveryScheduled;
        if (nowUtc > lesson.StartsAt.AddDays(Math.Max(1, recoveryDeadlineDays))) return LessonStatus.NotRecovered;
        if (record.Source == RecordSource.SelfReport) return LessonStatus.NotTaughtSelfReported;
        return record.Outcome == DutyOutcome.Excused ? LessonStatus.MissedWithPermission : LessonStatus.MissedWithoutPermission;
    }

    /// <summary>
    /// Maps lessons to items with status and what THIS caller may do. <paramref name="canFlag"/> answers "is the caller a
    /// lesson supervisor for this teacher" — computed by the caller from permission and staff scope, never stored.
    /// </summary>
    public static async Task<List<LessonItemDto>> ToItemsAsync(QMgrDbContext db, IReadOnlyList<StaffDuty> lessons, Guid callerId, Func<Guid, bool> canFlag,
        StaffPerformancePolicyDto policy, TimeZoneInfo zone, DateTime nowUtc, CancellationToken ct = default)
    {
        if (lessons.Count == 0) return new();
        var ids = lessons.Select(l => l.Id).ToList();
        var records = await db.StaffPerformanceRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.DutyId != null && ids.Contains(r.DutyId.Value) && r.Status == StaffRecordStatus.Final)
            .ToListAsync(ct);
        var recoveries = await db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.RecoversDutyId != null && ids.Contains(d.RecoversDutyId.Value) && d.IsActive)
            .ToListAsync(ct);
        var recoveryIds = recoveries.Select(r => r.Id).ToList();
        var recoveryRecords = recoveryIds.Count == 0 ? new List<StaffPerformanceRecord>() : await db.StaffPerformanceRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.DutyId != null && recoveryIds.Contains(r.DutyId.Value) && r.Status == StaffRecordStatus.Final).ToListAsync(ct);
        var recovered = lessons.Where(l => l.RecoversDutyId != null).Select(l => l.RecoversDutyId!.Value).ToList();
        var originals = recovered.Count == 0 ? new Dictionary<Guid, StaffDuty>() : await db.StaffDuties.IgnoreQueryFilters().AsNoTracking()
            .Where(d => recovered.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);

        var timetableLessonIds = lessons.Where(l => l.TimetableLessonId != null).Select(l => l.TimetableLessonId!.Value).Distinct().ToList();
        var periods = timetableLessonIds.Count == 0 ? new Dictionary<Guid, string>() : await db.TimetableLessons.IgnoreQueryFilters().AsNoTracking()
            .Where(l => timetableLessonIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.PeriodKey, ct);
        var subjectIds = lessons.Where(l => l.SubjectId != null).Select(l => l.SubjectId!.Value).Distinct().ToList();
        var subjects = await db.Subjects.IgnoreQueryFilters().AsNoTracking().Where(s => subjectIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => new { s.Name, s.Code }, ct);
        var names = await StaffLookups.LoadNamesAsync(db, lessons.Select(l => l.ExpectedUserIds?.FirstOrDefault()).Concat(records.Select(r => (Guid?)r.LoggedByUserId)), ct);

        var items = new List<LessonItemDto>();
        foreach (var l in lessons)
        {
            var teacher = l.ExpectedUserIds?.FirstOrDefault() ?? Guid.Empty;
            var record = records.Where(r => r.DutyId == l.Id && r.SubjectUserId == teacher).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            var recovery = recoveries.Where(r => r.RecoversDutyId == l.Id).OrderByDescending(r => r.StartsAt).FirstOrDefault();
            var recoveryRecord = recovery == null ? null : recoveryRecords.Where(r => r.DutyId == recovery.Id).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
            var status = StatusOf(l, record, recovery, recoveryRecord, nowUtc, policy.LessonRecoveryDeadlineDays);
            var started = l.StartsAt <= nowUtc;
            var isTeacher = teacher == callerId;
            var supervisor = !isTeacher && canFlag(teacher);
            var missed = record != null && IsMissed(record.Outcome);
            var subject = l.SubjectId is { } sid && subjects.TryGetValue(sid, out var sub) ? sub : null;
            var original = l.RecoversDutyId is { } rid && originals.TryGetValue(rid, out var o) ? o : null;
            items.Add(new LessonItemDto
            {
                DutyId = l.Id, StartsAt = l.StartsAt, EndsAt = l.EndsAt,
                PeriodLabel = l.TimetableLessonId is { } tl && periods.TryGetValue(tl, out var pk) ? pk : null,
                ClassName = l.ClassName ?? l.Title, SubjectId = l.SubjectId, SubjectName = subject?.Name ?? l.Title, SubjectCode = subject?.Code ?? "",
                Room = l.Room ?? l.Location, TeacherUserId = teacher, TeacherName = names[teacher] is { Length: > 0 } n ? n : "A former member of staff",
                Status = status, Outcome = record?.Outcome, FlagNote = record == null ? null : NoteOf(record.Description, l.Title),
                FlaggedByName = record == null ? null : names.Optional(record.LoggedByUserId), FlaggedAt = record?.CreatedAt,
                RecoversDutyId = l.RecoversDutyId,
                RecoversText = original == null ? null : string.Create(CultureInfo.InvariantCulture, $"Recovers {original.Title}, {TimeZoneInfo.ConvertTimeFromUtc(original.StartsAt, zone):ddd dd MMM yyyy HH:mm}"),
                RecoveryDutyId = recovery?.Id, RecoveryStartsAt = recovery?.StartsAt,
                RecoverBy = missed && l.RecoversDutyId == null ? l.StartsAt.AddDays(Math.Max(1, policy.LessonRecoveryDeadlineDays)) : null,
                CancelReason = l.IsActive ? null : l.Description,
                CanSelfReport = isTeacher && l.IsActive && started && (record == null || record.Source == RecordSource.SelfReport),
                CanFlag = supervisor && l.IsActive && started,
                CanScheduleRecovery = l.IsActive && missed && recovery == null && l.RecoversDutyId == null && (isTeacher || supervisor)
            });
        }
        return items.OrderBy(i => i.StartsAt).ThenBy(i => i.TeacherName).ToList();
    }

    private static string? NoteOf(string description, string title)
        => description.StartsWith(title + " — ", StringComparison.Ordinal) ? description[(title.Length + 3)..] : null;

    // ---- Flags (plan §7.3) ------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a lesson's flag for its teacher: annuls any earlier Final record for the lesson and inserts the new one,
    /// under the same advisory lock the register uses, so a teacher's "taught" and a supervisor's override at the same
    /// moment leave exactly one Final record. Closes the lesson's register. Returns the new record.
    /// </summary>
    public static async Task<StaffPerformanceRecord> WriteFlagAsync(QMgrDbContext db, StaffDuty lesson, PerformanceParameter parameter, DutyOutcome outcome, string? note,
        Guid actorId, RecordSource source, DateTime nowUtc, CancellationToken ct = default)
    {
        var teacher = lesson.ExpectedUserIds![0];
        StaffPerformanceRecord? written = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            if (written != null) db.Entry(written).State = EntityState.Detached;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var lockKey = $"staff-register:{lesson.Id}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey})::bigint)", ct);

            var existing = await db.StaffPerformanceRecords
                .Where(r => r.DutyId == lesson.Id && r.SubjectUserId == teacher && r.Status == StaffRecordStatus.Final).ToListAsync(ct);
            foreach (var old in existing)
            {
                old.Status = StaffRecordStatus.Annulled;
                old.UpdatedAt = nowUtc;
                old.UpdatedBy = actorId;
                db.StaffPerformanceNotes.Add(new StaffPerformanceNote { RecordId = old.Id, AuthorUserId = actorId, Kind = StaffNoteKind.Annulment, Body = "Lesson flag changed" });
            }

            var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            var description = trimmed == null ? lesson.Title : $"{lesson.Title} — {trimmed}";
            written = new StaffPerformanceRecord
            {
                OrganizationId = lesson.OrganizationId, BranchId = lesson.BranchId, SubjectUserId = teacher, ParameterId = parameter.Id, DutyId = lesson.Id,
                Outcome = outcome, Points = StaffDutiesController.PointsFor(parameter, outcome),
                Description = description.Length > 2000 ? description[..2000] : description,
                OccurredAt = lesson.StartsAt, Source = source, Status = StaffRecordStatus.Final, Visibility = WelfareVisibility.Standard,
                LoggedByUserId = actorId, CreatedBy = actorId
            };
            db.StaffPerformanceRecords.Add(written);

            var row = await db.StaffDuties.FirstAsync(d => d.Id == lesson.Id, ct);
            row.RegisterOpenedAt ??= nowUtc;
            row.RegisterClosedAt = nowUtc;
            row.RegisterClosedByUserId = actorId;
            row.UpdatedAt = nowUtc;
            row.UpdatedBy = actorId;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return written!;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
