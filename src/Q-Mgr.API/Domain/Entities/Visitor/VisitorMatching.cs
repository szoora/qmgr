using System.Text.RegularExpressions;

namespace QMgr.Domain.Entities.Visitor;

/// <summary>
/// Normalization rules for matching a returning visitor by phone/email/ID, shared between
/// profile creation and search so both sides of a lookup are normalized the same way.
/// </summary>
public static partial class VisitorMatching
{
    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    // Strips everything but digits and a leading '+', so "+254 753 404 044", "0753-404-044"
    // and "0753404044" all normalize to comparable forms. Not a full E.164 parser — good enough
    // to catch the formatting variance a front-desk typist actually introduces.
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = NonPhoneCharsRegex().Replace(phone.Trim(), "");
        return string.IsNullOrEmpty(digits) ? null : digits;
    }

    public static string? NormalizeIdNumber(string? idNumber) =>
        string.IsNullOrWhiteSpace(idNumber) ? null : IdWhitespaceRegex().Replace(idNumber.Trim().ToUpperInvariant(), "");

    [GeneratedRegex(@"[^\d+]")]
    private static partial Regex NonPhoneCharsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex IdWhitespaceRegex();
}
