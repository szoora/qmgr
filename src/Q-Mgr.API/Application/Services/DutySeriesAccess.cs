using QMgr.Domain.Entities.Staff;

namespace QMgr.API.Application.Services;

/// <summary>
/// Who may change a named (exam-supervision) duty series — the ONE home for it (2026-09-23), the counterpart of
/// <see cref="TimetableAccess"/> for a timetable.
///
/// <para><b>A manager, or a holder of <c>staff.duties.manage</c>.</b> Byte for byte the rule <c>RecorderUserIds</c>
/// and a timetable's <c>ManagerUserIds</c> already follow: delegation is not scope, so a teacher appointed to run the
/// end-of-term exams writes that series without the permission, and nothing else.</para>
///
/// <para><b>Appointing managers is the permission holder's act alone</b>, or the control is decoration: a manager
/// who could appoint another could hand the series to anybody.</para>
///
/// <para>A series keeps its managers IN every slot's <c>RecorderUserIds</c> as well, so the register, the portal's
/// to-do, the register chase and the digest — every reader of recorders — treat a manager as the person who takes the
/// register without being taught about series. <see cref="Recorders"/> is how a manager change is applied.</para>
/// </summary>
public static class DutySeriesAccess
{
    public static bool IsManager(StaffDuty slot, Guid userId) => slot.SeriesManagerUserIds.Contains(userId);

    public static bool MayWrite(StaffDuty slot, Guid userId, bool holdsManage) => holdsManage || IsManager(slot, userId);

    /// <summary>A slot's recorders once the series' managers change from <paramref name="before"/> to
    /// <paramref name="after"/>: the old managers out, the new ones in, anybody named in their own right kept.</summary>
    public static Guid[] Recorders(IEnumerable<Guid> current, IEnumerable<Guid> before, IEnumerable<Guid> after)
        => current.Except(before).Concat(after).Distinct().ToArray();
}
