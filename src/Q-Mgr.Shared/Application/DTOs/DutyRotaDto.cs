using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Enums;

namespace QMgr.Application.DTOs;

// =====================================================================================================
// Duty rota, duty reports, subjects, the timetable and lessons — the DTO contract between Q-Mgr.API and
// Q-Mgr.Web. Plan: docs/plans/DUTY_ROTA_AND_TIMETABLE.md. Onboarding lives in StaffOnboardingDto.cs.
// =====================================================================================================

// ---- Policy additions (plan §3.1: StaffPerformancePolicyDto gains these) ---------------------------------

/// <summary>What an escalating reminder is about (plan §8.1). Persisted inside the policy blob: append only.</summary>
public enum ReminderSubject
{
    /// <summary>A rota slot is approaching (plan §4.2).</summary>
    RotaStart = 0,
    /// <summary>A duty report has reached, or passed, its due time (plan §4.4).</summary>
    ReportDue = 1,
    /// <summary>A lesson is about to start (plan §7.2).</summary>
    LessonStart = 2,
    /// <summary>A published timetable has a new clash (plan §6.3).</summary>
    TimetableClash = 3,
    /// <summary>A Session duty (meeting, invigilation) is approaching — the pre-existing single-shot reminder, moved onto the ladder.</summary>
    SessionStart = 4,
    /// <summary>A duty has ended with its register not taken — the pre-existing chase, moved onto the ladder.</summary>
    RegisterChase = 5
}

/// <summary>Where a stage goes. Flags: a stage may ring the bell AND send an email.</summary>
[Flags]
public enum ReminderChannels
{
    None = 0,
    /// <summary>Only a line in the person's next digest (plan §4.2 stage 1).</summary>
    Digest = 1,
    Bell = 2,
    Email = 4,
    /// <summary>Only where the tenant has SMS on and the person allows it; never carries a student's name.</summary>
    Sms = 8
}

/// <summary>Who a stage reaches besides the person it is about.</summary>
[Flags]
public enum ReminderAudience
{
    /// <summary>The person on duty, the report's author, the teacher of the lesson.</summary>
    Subject = 1,
    /// <summary>The slot's administrator on duty (a to-do line), or a lesson supervisor.</summary>
    Supervisor = 2,
    /// <summary>Head and director of studies: a line in their daily digest.</summary>
    Heads = 4,
    /// <summary>Timetable masters (holders of timetable.manage).</summary>
    TimetableMasters = 8
}

public record ReminderStageDto
{
    /// <summary>1-based, ascending; the row stores the highest stage sent.</summary>
    public int Stage { get; set; }
    /// <summary>Minutes relative to the anchor: negative before a start, positive after a due time.</summary>
    public int OffsetMinutes { get; set; }
    /// <summary>When set, the stage is sent at this branch-local hour on the day the offset lands ("1 day before, at the morning hour").</summary>
    public int? AtLocalHour { get; set; }
    public ReminderChannels Channels { get; set; } = ReminderChannels.Bell;
    public ReminderAudience Audience { get; set; } = ReminderAudience.Subject;
    /// <summary>May break quiet hours — by definition the next hour or two (plan §4.2 stage 4).</summary>
    public bool Interruptive { get; set; }
}

public record ReminderLadderDto
{
    public ReminderSubject Subject { get; set; }
    public List<ReminderStageDto> Stages { get; set; } = new();
}

public record QuietHoursDto
{
    public bool Enabled { get; set; } = true;
    /// <summary>Branch-local hour quiet time begins (default 20:00).</summary>
    public int StartHour { get; set; } = 20;
    /// <summary>Branch-local hour it ends and held stages go out (default 06:00).</summary>
    public int EndHour { get; set; } = 6;
    /// <summary>"The policy's morning hour" a stage may be pinned to (default 07:00).</summary>
    public int MorningHour { get; set; } = 7;
}

public enum DutyReportSectionKind { Text = 0, Choice = 1 }

public record DutyReportSectionDto
{
    [MaxLength(40)] public string Key { get; set; } = string.Empty;
    [MaxLength(100)] public string Title { get; set; } = string.Empty;
    [MaxLength(300)] public string? Hint { get; set; }
    public DutyReportSectionKind Kind { get; set; } = DutyReportSectionKind.Text;
    public List<string> Choices { get; set; } = new();
    public bool Required { get; set; }
}

public record DutyReportDefaultsDto
{
    /// <summary>Default report cadence for a slot of one day (plan §15 decision 1: daily).</summary>
    public ReportCadence DayLongCadence { get; set; } = ReportCadence.Daily;
    /// <summary>For a slot of about a week (decision 1: weekly).</summary>
    public ReportCadence WeekLongCadence { get; set; } = ReportCadence.Weekly;
    /// <summary>For a slot of about a month (decision 1: monthly).</summary>
    public ReportCadence MonthLongCadence { get; set; } = ReportCadence.Monthly;
    /// <summary>Branch-local due time of each report period ("18:00").</summary>
    public string DueLocalTime { get; set; } = "18:00";
    /// <summary>Plan §4.3 / decision 2: off — the teacher on duty does not read the supervisor's report about them.</summary>
    public bool OnDutyMayReadSupervisorReport { get; set; }
    /// <summary>Fairness warning: more rota slots than this in a term for one person (plan §4.1).</summary>
    public int MaxRotaSlotsPerTerm { get; set; } = 6;
}

public record TeachingLoadNormsDto
{
    /// <summary>MoES: 20–24 lessons a week.</summary>
    public int MinLessonsPerWeek { get; set; } = 20;
    public int MaxLessonsPerWeek { get; set; } = 24;
    /// <summary>The minimum for a teacher on both A- and O-Level (plan §15 decision 7: 18).</summary>
    public int MinLessonsPerWeekMixedLevel { get; set; } = 18;
    public int MaxPeriodsPerDay { get; set; } = 8;
    public int MaxConsecutivePeriods { get; set; } = 4;
}

// ---- Subjects (plan §5.2) --------------------------------------------------------------------------------

public record SubjectDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public string? Color { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }
    /// <summary>Live subject-teacher assignments in the current branch.</summary>
    public int TeacherCount { get; init; }
}

public record SaveSubjectRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(20)] public string Code { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    [MaxLength(9)] public string? Color { get; set; }
    public int SortOrder { get; set; }
}

public record AssignSubjectTeacherRequest
{
    [Required, MaxLength(100)] public string ClassName { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid SubjectId { get; set; }
    [Range(0, 60)] public int? PeriodsPerWeek { get; set; }
}

/// <summary>A teacher's teaching, for a profile and the portal: "Mathematics — S2A, S2B (12 periods)".</summary>
public record TeachingSummaryDto
{
    public Guid SubjectId { get; init; }
    public string SubjectName { get; init; } = string.Empty;
    public string SubjectCode { get; init; } = string.Empty;
    public List<string> Classes { get; init; } = new();
    public int PlannedPeriodsPerWeek { get; init; }
    public int SortOrder { get; init; }
    /// <summary>Periods the published timetable places for these assignments (0 until a timetable is published).</summary>
    public int TimetabledPeriodsPerWeek { get; init; }
}
