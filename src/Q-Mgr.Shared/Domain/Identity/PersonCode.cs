namespace QMgr.Domain.Identity;

/// <summary>
/// THE ONE HOME for a school's own identifier for a person: a student's admission number
/// (<c>Student.StudentCode</c>) and a member of staff's staff number (<c>User.EmployeeNumber</c>).
///
/// <para>Both are the same thing wearing two names — a short code the school issues, unique within
/// that organisation, and the key every import, every re-import and every paper record matches on.
/// Before this class each write path did its own <c>Trim()</c> and its own duplicate query, and
/// several did neither: <c>UsersController.CreateUser</c> and <c>UpdateUser</c> wrote whatever they
/// were given and left the database's unique index to throw, which reaches the browser as a 500 with
/// no field named. That is this codebase's most-repeated bug class (see the DTO-duplication and
/// <c>RegistrationIdentity</c> notes in CLAUDE.md), so there is one copy of the rule and everything
/// calls it — the API, the import processor and the Web's own pre-submit checks.</para>
///
/// <para><b>Uniqueness is CASE-INSENSITIVE and that is enforced by the database</b>, through a
/// functional unique index on <c>upper(column)</c> per organisation. No Postgres extension is
/// involved (the standing no-extensions rule rules out <c>citext</c>), and no second normalised
/// column was added — <c>upper()</c> is plain SQL and the index serves the lookups below. What the
/// school TYPED is what is stored and shown; only the comparison is folded.</para>
/// </summary>
public static class PersonCode
{
    /// <summary>A staff number fits <c>User.EmployeeNumber</c>; a student code's column is wider.</summary>
    public const int MaxStaffNumberLength = 50;

    /// <summary>The <c>Student.StudentCode</c> column's own width.</summary>
    public const int MaxStudentCodeLength = 100;

    /// <summary>Shortest a code may be. One character is a typo far more often than a roll number.</summary>
    public const int MinLength = 2;

    /// <summary>
    /// What is STORED: trimmed, with any internal run of whitespace collapsed to one space. Empty
    /// becomes null, so "  " and a missing value are the same thing to every caller.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// What is COMPARED. Folding the case here is what makes "st/001" and "ST/001" one student
    /// rather than two, and it must stay identical to the <c>upper()</c> in the database index or a
    /// duplicate the API accepts will be refused by Postgres as a 500 instead.
    /// </summary>
    public static string? Key(string? value) => Normalize(value)?.ToUpperInvariant();

    /// <summary>
    /// The characters a code may contain. Letters and digits are the code; the four punctuation
    /// marks and a space are the separators schools actually use ("MH/S/001", "2026-014", "P.7 A").
    /// Everything else is refused — including the four characters a spreadsheet reads as the start
    /// of a formula, which <see cref="QMgr.Application.Import.ImportRules.NeutraliseFormula"/> has
    /// to strip on the way OUT of an export. Refusing them on the way in is the better half of that.
    /// </summary>
    public static bool IsAllowed(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '/' || c == '.' || c == '_' || c == ' ';

    /// <summary>
    /// Why this code cannot be used, or null when it can. <paramref name="label"/> names the field
    /// as the person reading it knows it ("Student code", "Staff number") so one sentence can be
    /// shown verbatim by a form, an import's per-row reason and the API's problem detail alike.
    /// </summary>
    public static string? Validate(string? value, string label, int maxLength)
    {
        var code = Normalize(value);
        if (code == null) return $"{label} is required.";
        if (code.Length < MinLength) return $"{label} must be at least {MinLength} characters.";
        if (code.Length > maxLength) return $"{label} cannot be longer than {maxLength} characters.";
        if (!code.Any(char.IsLetterOrDigit)) return $"{label} must contain at least one letter or number.";

        var bad = code.Where(c => !IsAllowed(c)).Distinct().ToArray();
        if (bad.Length > 0)
            return $"{label} may only contain letters, numbers, and - / . _ — remove {string.Join(" ", bad.Select(c => $"'{c}'"))}.";

        return null;
    }

    /// <summary>A staff number, at its own column width.</summary>
    public static string? ValidateStaffNumber(string? value, string label = "Staff number")
        => Validate(value, label, MaxStaffNumberLength);

    /// <summary>A student's admission number, at its own column width.</summary>
    public static string? ValidateStudentCode(string? value, string label = "Student code")
        => Validate(value, label, MaxStudentCodeLength);
}
