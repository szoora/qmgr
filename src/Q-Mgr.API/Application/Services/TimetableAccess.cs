using QMgr.Domain.Entities.Staff;

namespace QMgr.API.Application.Services;

/// <summary>
/// WHO MAY WRITE A TIMETABLE VERSION (2026-09-22). The ONE home for that question, called by every write
/// endpoint on TimetableController, by the exception endpoints, and by the self-service decider rule.
///
/// THE RULE IS ONE LINE:
///
///     caller ∈ timetable.ManagerUserIds  ||  caller holds timetable.manage
///
/// which is byte for byte the delegation rule <see cref="StaffDuty.RecorderUserIds"/> has had since the duty
/// rota shipped — <c>caller ∈ RecorderUserIds || caller holds staff.duties.manage</c>. The timetable was the
/// outlier, not this.
///
/// WHY IT IS NOT AN ATTRIBUTE. <c>[RequirePermission(Permissions.TimetableManage)]</c> refuses before the
/// handler runs, so it cannot let an appointed manager who holds no permission through. Every write endpoint
/// therefore drops the attribute and calls <see cref="Refuse"/> instead. An endpoint that keeps the attribute
/// silently excludes the very people this feature exists for — which is the mistake to watch for when adding one.
///
/// THE ADMINISTRATOR OVERRIDE IS DELIBERATE AND IS NEVER SILENT. A permission holder who is not named may
/// still write the version: a school whose timetable master leaves mid-term, is ill, or leaves a draft locked
/// must not be shut out of its own timetable, and a system that can be bricked by one person's absence gets
/// worked around with a shared login — worse than the problem. So the abuse concern is answered by
/// <see cref="IsOverride"/>: the write is recorded as <c>ActivityActions.TimetableOverridden</c> and every
/// named manager is told. That is AC-5's maker–checker intent without a two-person approval workflow a school
/// of forty staff will not run.
///
/// APPOINTING IS NOT WRITING. <see cref="MayAppoint"/> is the permission alone — a manager may not add or
/// remove managers, including themselves. Same asymmetry as RoleAssignmentGuard.
/// </summary>
public static class TimetableAccess
{
    public static bool IsManager(Timetable timetable, Guid userId)
        => timetable.ManagerUserIds.Contains(userId);

    /// <summary>The whole rule. Nothing else may decide this.</summary>
    public static bool MayWrite(Timetable timetable, Guid userId, bool holdsTimetableManage)
        => holdsTimetableManage || IsManager(timetable, userId);

    /// <summary>
    /// True when this write only happens because of the permission, and somebody else is named. False when
    /// nobody is named at all: there is no manager to override, and calling an ordinary write an override
    /// would put a line in the log for every version of every branch that has never been appointed.
    /// </summary>
    public static bool IsOverride(Timetable timetable, Guid userId, bool holdsTimetableManage)
        => holdsTimetableManage && timetable.ManagerUserIds.Length > 0 && !IsManager(timetable, userId);

    /// <summary>Appointing, and un-appointing, is the permission holder's act alone.</summary>
    public static bool MayAppoint(bool holdsTimetableManage) => holdsTimetableManage;

    /// <summary>
    /// AN APPOINTED MANAGER IS NOT SUBJECT TO THE STAFF-SCOPE REFUSAL, and this is the rule that makes the whole
    /// feature reachable rather than theoretical.
    ///
    /// Every timetable write already refused a caller whose <c>StaffScope</c> is narrower than the organization,
    /// because publishing materialises lessons for the WHOLE school in a background job that cannot be row-scoped
    /// downstream. That reasoning is sound and unchanged for a permission holder.
    ///
    /// But the seeded <c>teacher</c> role is <c>StaffScope.SelfOnly</c> — and a teacher is exactly who a school
    /// appoints as its timetable master. Applying the scope refusal to them would have 403'd the appointed master
    /// before the ownership rule was ever consulted, so the feature would have been unreachable for the only
    /// person it was built for. The first cut of this did precisely that.
    ///
    /// The appointment IS the unscoping: it is an explicit, per-object grant made by somebody who holds
    /// <c>timetable.manage</c> and an unscoped view, saying "this person builds the whole school's timetable".
    /// That is stronger evidence than a role's default scope, and it is the same call this codebase already makes
    /// for <see cref="StaffDuty.RecorderUserIds"/>: <b>delegation is not scope</b>, so a SelfOnly teacher named
    /// recorder of a meeting marks the whole room's register.
    /// </summary>
    public static bool NeedsUnscopedStaffView(Timetable timetable, Guid userId) => !IsManager(timetable, userId);

    /// <summary>
    /// Why this caller may not write it, in the reader's own terms, or null when they may. It names who the
    /// version belongs to, because "you cannot edit this" without saying whose it is sends somebody to ask
    /// the wrong person.
    /// </summary>
    public static string? Refuse(Timetable timetable, Guid userId, bool holdsTimetableManage, IReadOnlyDictionary<Guid, string>? names = null)
    {
        if (MayWrite(timetable, userId, holdsTimetableManage)) return null;

        if (timetable.ManagerUserIds.Length == 0)
            return "Building a timetable needs the timetable permission.";

        var who = names == null
            ? null
            : string.Join(", ", timetable.ManagerUserIds.Select(id => names.GetValueOrDefault(id)).Where(n => !string.IsNullOrWhiteSpace(n)));

        return string.IsNullOrWhiteSpace(who)
            ? "This timetable has an appointed master. Ask them, or somebody with the timetable permission."
            : $"{who} is the appointed master of this timetable. Ask them, or somebody with the timetable permission.";
    }

    /// <summary>
    /// The people a change to this version has to be reported to: its named managers, never the person who
    /// made the change. Empty when nobody is named, or when the writer IS the only manager.
    /// </summary>
    public static IReadOnlyList<Guid> TellAbout(Timetable timetable, Guid actorId)
        => timetable.ManagerUserIds.Where(id => id != actorId).Distinct().ToList();
}
