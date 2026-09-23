using System.ComponentModel.DataAnnotations;
using QMgr.Domain.Common;
using QMgr.Domain.Enums;

namespace QMgr.Domain.Entities.Staff;

/// <summary>
/// What is measured: Lesson Attendance, Exam Supervision, Meeting Attendance, Co-curricular
/// Activity, Recognition, Conduct, Wellbeing … The staff analogue of <c>WelfareCategory</c>, and
/// deliberately a separate table: a Subject discriminator on WelfareCategory would put a child-
/// behaviour taxonomy and a staff-duty taxonomy in one list, one editor and one FK target that
/// StudentFlag also reads. Same shape, separate subject.
///
/// Org-scoped like WelfareCategory (one taxonomy across campuses), Restrict on delete so history
/// cannot be erased, retired with IsActive. Seeded per tenant with the MoES set on first use.
/// </summary>
public class PerformanceParameter : BaseAuditableEntity
{
    public Guid OrganizationId { get; set; }

    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public ParameterKind Kind { get; set; } = ParameterKind.Contribution;

    /// <summary>
    /// The staff group this applies to, by NAME, from StaffPerformancePolicyDto.StaffGroups.
    /// <b>Null means every group.</b> Was a three-value enum resolved from the role code until
    /// 2026-09-22 — see StaffGroups for why that made the bursar teaching staff.
    /// </summary>
    [MaxLength(60)]
    public string? AppliesToGroup { get; set; }

    /// <summary>Signed default points. Null for Wellbeing, which is never scored. Pre-fills; never locks.</summary>
    public int? DefaultPoints { get; set; }

    /// <summary>Largest magnitude a single record may carry.</summary>
    public int MaxPointsPerEntry { get; set; } = 10;

    /// <summary>The per-period cap that stops one parameter dominating the composite. Null = uncapped.</summary>
    public int? MaxPointsPerPeriod { get; set; }

    /// <summary>Share of the composite. 0 = evidence only, never scored.</summary>
    public decimal Weight { get; set; } = 1;

    /// <summary>4 for an observation rubric; null for anything without a scale.</summary>
    public int? RatingScale { get; set; }

    /// <summary>JSON array of one descriptor per level, lowest first. Shown on the observation form.</summary>
    public string? RubricJson { get; set; }

    public WelfareVisibility DefaultVisibility { get; set; } = WelfareVisibility.Standard;

    /// <summary>
    /// The purpose the data is collected for, shown on every record ("collected for cover planning
    /// and appraisal evidence"). Purpose limitation, written down at design time and carried on the
    /// row, per the Data Protection and Privacy Act's s.3 principles.
    /// </summary>
    [MaxLength(300)]
    public string Purpose { get; set; } = string.Empty;

    [MaxLength(9)]
    public string? Color { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// Credited automatically from another module's activity (a welfare record filed, a token served,
    /// a visitor hosted) when the tenant's policy has SystemAwardsEnabled. Off by default.
    /// </summary>
    public bool IsSystemSource { get; set; }

    /// <summary>
    /// The Attendance or Duty parameter this one OFFSETS. A record here counts as one recovered
    /// occasion on that parameter's attendance score — the MoES Lesson Recovery Schedule, where "a
    /// recovered lesson should not be considered as a lesson missed". Seeded on "Lesson Recovery" →
    /// "Lesson Attendance". A nullable column rather than a kind or a name match: a school that calls
    /// it "Make-up Lessons" must still get the offset, and a second recovery parameter for exam
    /// supervision must be able to point somewhere else.
    /// </summary>
    public Guid? OffsetsParameterId { get; set; }

    public virtual Organization.Organization? Organization { get; set; }
    public virtual ICollection<StaffPerformanceRecord> Records { get; set; } = new List<StaffPerformanceRecord>();
}
