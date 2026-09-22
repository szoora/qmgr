using QMgr.Application.DTOs;
using QMgr.Application.Import;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Identity;

namespace QMgr.API.Application.Services;

/// <summary>One field this file would overwrite: what to call it, and how to write it.</summary>
public readonly record struct StaffFieldChange(string Label, Action<User> Apply);

/// <summary>
/// WHAT A STAFF IMPORT WOULD CHANGE ABOUT SOMEBODY ALREADY ON FILE — the one home for the rule AND
/// for the words it is reported in.
///
/// <para>The precheck reads the labels to tell the reader "this file moves 14 phone numbers and 3
/// job titles" before anything is sent; the import runs the same list and applies it. They cannot
/// drift, because there is one list — a row the preview called unchanged and the import then changed
/// is the worst answer an import can give, and this codebase has already learned that lesson twice
/// (ImportRules, docs/plans/BULK_IMPORT_SYSTEM.md).</para>
///
/// <para>A change is returned as an action rather than applied here, so the precheck cannot write by
/// accident however the entity reached it.</para>
/// </summary>
public static class StaffImportChanges
{
    /// <summary>
    /// The row's first and last name. A row may arrive with ONE combined name instead of two — that
    /// is how most school exports are written ("Staff Name: Abaho Jude") — and the server never
    /// invents the order: it uses the one the batch carries. The precheck and the import both read a
    /// name through here, or they could disagree about whose record a row lands on.
    /// </summary>
    public static (string First, string Last) NamesOf(StaffImportRow row, NameOrder nameOrder)
    {
        var first = (row.FirstName ?? "").Trim();
        var last = (row.LastName ?? "").Trim();

        if ((first.Length == 0 || last.Length == 0) && !string.IsNullOrWhiteSpace(row.FullName))
        {
            var parts = PersonName.Split(row.FullName, nameOrder);
            if (first.Length == 0) first = parts.GivenName;
            if (last.Length == 0) last = parts.FamilyName;
        }
        return (first, last);
    }

    /// <summary>
    /// The fields <paramref name="row"/> would overwrite on <paramref name="user"/>. A value the file
    /// left blank never changes anything: a sheet exported from a system that does not hold national
    /// IDs must not blank the school's.
    /// </summary>
    public static List<StaffFieldChange> Compute(StaffImportRow row, string? firstName, string? lastName, User user)
    {
        var changes = new List<StaffFieldChange>();

        void Text(string label, string? value, string? stored, Action<User, string> apply)
        {
            if (!ImportMatching.Differs(value, stored)) return;
            var v = value!.Trim();
            changes.Add(new StaffFieldChange(label, u => apply(u, v)));
        }

        Text("first name", firstName, user.FirstName, (u, v) => u.FirstName = v);
        Text("surname", lastName, user.LastName, (u, v) => u.LastName = v);
        Text("phone", row.Phone, user.Phone, (u, v) => u.Phone = v);
        Text("staff number", row.EmployeeNumber, user.EmployeeNumber, (u, v) => u.EmployeeNumber = v);
        Text("job title", row.JobTitle, user.JobTitle, (u, v) => u.JobTitle = v);
        Text("qualification", row.Qualification, user.Qualification, (u, v) => u.Qualification = v);
        Text("registration number", row.TeachingRegistrationNumber, user.TeachingRegistrationNumber, (u, v) => u.TeachingRegistrationNumber = v);
        Text("national ID", row.NationalId, user.NationalId, (u, v) => u.NationalId = v);
        Text("emergency contact", row.EmergencyContactName, user.EmergencyContactName, (u, v) => u.EmergencyContactName = v);
        Text("emergency phone", row.EmergencyContactPhone, user.EmergencyContactPhone, (u, v) => u.EmergencyContactPhone = v);

        if (StaffFieldParsing.Date(row.StartDate) is { } start && user.EmploymentStartDate != start)
            changes.Add(new StaffFieldChange("start date", u => u.EmploymentStartDate = start));
        if (StaffFieldParsing.Date(row.EndDate) is { } end && user.EmploymentEndDate != end)
            changes.Add(new StaffFieldChange("end date", u => u.EmploymentEndDate = end));
        if (StaffFieldParsing.Date(row.DateOfBirth) is { } dob && user.DateOfBirth != dob)
            changes.Add(new StaffFieldChange("date of birth", u => u.DateOfBirth = dob));
        if (StaffFieldParsing.EmploymentType(row.EmploymentType) is { } terms && user.EmploymentType != terms)
            changes.Add(new StaffFieldChange("employment terms", u => u.EmploymentType = terms));
        if (StaffFieldParsing.Sex(row.Sex) is { } sex && user.Sex != sex)
            changes.Add(new StaffFieldChange("sex", u => u.Sex = sex));

        return changes;
    }
}
