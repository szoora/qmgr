namespace QMgr.Domain.Enums;

/// <summary>
/// How widely a welfare record, a student flag, or a student-level note may be seen.
///
/// Replaces the old <c>WelfareRecord.Confidential</c> bool. A second bool beside the first would
/// have given four states for three meanings, and the fourth (Restricted but not Confidential) is
/// nonsense some read path would eventually get wrong — this codebase has no auto-mapper to hide a
/// missed branch, so an impossible combination becomes a silent runtime leak rather than a compile
/// error. One ordered enum cannot express it.
///
/// The rungs are ordered by restriction, and holding a lower key does NOT imply the higher one:
/// <c>welfare.view</c> does not grant Confidential, and <c>welfare.confidential.view</c> does not
/// grant Restricted. Each level is its own explicit permission, so a school decides deliberately
/// who sits on each.
/// </summary>
public enum WelfareVisibility
{
    /// <summary>
    /// Achievements, behaviour incidents, support plans. Visible to anyone holding
    /// <c>welfare.view</c> in the branch — narrowed to their own classes for a class-scoped role.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Safeguarding. Forced server-side for <c>WelfareCaseType.Welfare</c> regardless of what the
    /// client sends, exactly as the old bool was. Requires <c>welfare.confidential.view</c>.
    /// A class teacher never holds this, is never notified about one, and never sees it on a
    /// timeline — the DSL/administrator holds it alone (user decision, 2026-09-09).
    /// </summary>
    Confidential = 1,

    /// <summary>
    /// Administrator only. Requires <c>welfare.restricted.view</c>, which is seeded to Tenant Admin
    /// and SuperAdmin and to nobody else — a tenant must consciously grant it to a custom role.
    /// Never forced automatically: something becomes Restricted only because a person with the
    /// permission said so.
    /// </summary>
    Restricted = 2
}
