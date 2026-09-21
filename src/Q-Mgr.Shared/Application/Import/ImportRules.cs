namespace QMgr.Application.Import;

/// <summary>
/// THE VALIDATORS BOTH SIDES RUN. The browser checks a sheet's rows so a person can fix the file
/// before sending it; the server checks the rows it is actually sent, because a client can post
/// whatever it likes. Those are two different jobs, but they must be the SAME RULE — a row the
/// preview called good and the import then refuses, for a reason worded differently, is the worst
/// answer an import can give.
///
/// Keeping one copy here is the same decision as <c>RegistrationIdentity</c> and
/// <c>StaffFieldParsing</c> beside it: this codebase's most-repeated bug is a second copy of a rule
/// that then drifts from the first (see the DTO-duplication and column-alias-map notes in CLAUDE.md).
/// </summary>
public static class ImportRules
{
    /// <summary>
    /// RFC-shaped enough to catch a typo without refusing a valid oddity. Deliberately NOT a full
    /// RFC 5322 parser: an address this refuses is a row somebody has to fix by hand, so it errs
    /// towards accepting anything that could plausibly deliver.
    /// </summary>
    public static bool LooksLikeEmail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var v = s.Trim();
        var at = v.IndexOf('@');
        if (at <= 0 || at != v.LastIndexOf('@')) return false;
        var domain = v[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.')
               && !v.Contains(' ') && v.Length <= 254;
    }

    /// <summary>
    /// Ugandan mobile numbers in any of the shapes a school actually types: 0770…, +256770…,
    /// 256770…, with spaces, dashes or brackets. Returns null when it cannot be read as a phone
    /// number at all. An international number we do not recognise is KEPT rather than refused — a
    /// number we cannot classify is still somebody's number.
    /// </summary>
    public static string? NormalizePhone(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (digits.Length < 9) return null;
        if (digits.StartsWith("256") && digits.Length == 12) return "+" + digits;
        if (digits.StartsWith("0") && digits.Length == 10) return "+256" + digits[1..];
        if (digits.Length == 9) return "+256" + digits;
        return "+" + digits;
    }

    /// <summary>
    /// Neutralises a value that a spreadsheet would run as a FORMULA. OWASP CSV Injection / CWE-1236:
    /// a cell beginning <c>=</c>, <c>+</c>, <c>@</c>, a tab or a carriage return is executed by Excel
    /// and LibreOffice when the file is opened, and quoting the field does not stop it — only a
    /// leading apostrophe does.
    ///
    /// <para><b>A leading minus is treated carefully</b>: "-5" and "-12.50" are numbers a person
    /// expects to see in a spreadsheet, so only a minus followed by something non-numeric is
    /// neutralised. Mangling every negative figure to stop a formula nobody wrote would make the
    /// export wrong in the ordinary case to prevent the rare one.</para>
    /// </summary>
    public static string NeutraliseFormula(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var first = value[0];
        if (first is '=' or '+' or '\t' or '\r') return "'" + value;
        if (first == '@') return "'" + value;
        if (first == '-' && !double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return "'" + value;
        return value;
    }
}
