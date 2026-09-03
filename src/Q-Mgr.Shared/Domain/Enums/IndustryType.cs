namespace QMgr.Domain.Enums;

/// <summary>
/// Defines the industry type for the organization, which affects kiosk customization and UI
/// theming. Consolidated 2026-09-02 from 9 narrow verticals (Hospital/Bank/Pharmacy/Retail/
/// Government/Telecom/Restaurant/School/General) down to 5 broader categories, per the user's own
/// framing: most of the old list was "a duplication of Service" from a queue-management
/// perspective — differentiating a bank from a restaurant added categories without adding real
/// functional difference, since admins customize their own service types after signup anyway.
/// Only Health and Education stayed genuinely distinct (real regulatory/workflow differences:
/// patient care, student records). See migration AddIndustryCategoryConsolidation for how existing
/// Organization.IndustryType / DocArticle.Industry rows were remapped — this was a real data
/// migration, not just a rename, since the underlying column is a raw int.
/// </summary>
public enum IndustryType
{
    /// <summary>
    /// General service/walk-in counter business (retail, restaurant, telecom shop, or anything
    /// without a more specific category) — the default/catch-all.
    /// </summary>
    Service = 0,

    /// <summary>
    /// Office/paperwork-driven business — banks, government offices, and similar
    /// appointment/document-centric organizations.
    /// </summary>
    Business = 1,

    /// <summary>
    /// Healthcare — hospitals, clinics, pharmacies. Kept distinct from Service for genuinely
    /// different regulatory/workflow needs (patient care, prescriptions).
    /// </summary>
    Health = 2,

    /// <summary>
    /// Schools and academic institutions. Kept distinct from Service for genuinely different
    /// workflow needs (student records) — the natural fit for the Visitor & Safeguarding module's
    /// Student Roster/Welfare Ledger.
    /// </summary>
    Education = 3,

    /// <summary>
    /// Telecom, ISPs, and other communications-infrastructure businesses.
    /// </summary>
    Communications = 4
}
