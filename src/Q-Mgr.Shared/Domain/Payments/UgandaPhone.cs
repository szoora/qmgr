using System.Text.RegularExpressions;

namespace QMgr.Domain.Payments;

/// <summary>
/// The ONE home for "is this a mobile money number the gateway will accept" (2026-09-19).
///
/// It mirrors the sacc.ug gateway's own rule, <c>PhoneNumberHelper</c> in CRMApi, exactly: strip
/// spaces, dashes, brackets and a leading plus; a ten-digit number starting 0 becomes 256 plus the
/// last nine digits; the result must be twelve digits starting 256 whose next two digits are a known
/// operator prefix. Checking the same rule here means a number the gateway would refuse is refused
/// before a ledger row, an invoice or a prompt is created for it — and the Web shows the same verdict
/// as the person types, because both sides call this class.
///
/// If the gateway's prefixes change, change them here and nowhere else.
/// </summary>
public static partial class UgandaPhone
{
    public const string CountryCode = "256";

    private static readonly string[] MtnPrefixes = { "77", "78", "31", "39", "76", "79" };
    private static readonly string[] AirtelPrefixes = { "70", "75", "74", "20" };
    private static readonly string[] UtlPrefixes = { "71" };
    private static readonly string[] LycamobilePrefixes = { "72" };

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex NonDigits();

    /// <summary>256XXXXXXXXX, or the digits as given when they cannot be normalised.</summary>
    public static string Normalize(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) return string.Empty;
        var digits = NonDigits().Replace(phoneNumber.Trim(), "");
        if (digits.StartsWith('0') && digits.Length == 10) return CountryCode + digits[1..];
        if (digits.Length == 9 && !digits.StartsWith('0')) return CountryCode + digits;
        return digits;
    }

    /// <summary>True when the gateway would accept this number.</summary>
    public static bool IsValid(string? phoneNumber) => Operator(phoneNumber) != null;

    /// <summary>"MTN", "Airtel", "UTL", "Lycamobile", or null for a number the gateway would refuse.</summary>
    public static string? Operator(string? phoneNumber)
    {
        var n = Normalize(phoneNumber);
        if (n.Length != 12 || !n.StartsWith(CountryCode, StringComparison.Ordinal)) return null;
        var prefix = n.Substring(3, 2);
        if (MtnPrefixes.Contains(prefix)) return "MTN";
        if (AirtelPrefixes.Contains(prefix)) return "Airtel";
        if (UtlPrefixes.Contains(prefix)) return "UTL";
        if (LycamobilePrefixes.Contains(prefix)) return "Lycamobile";
        return null;
    }

    /// <summary>The problem with a number in words a person can act on, or null when it is fine.</summary>
    public static string? Problem(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) return "Enter a mobile money number.";
        var n = Normalize(phoneNumber);
        if (n.Length != 12 || !n.StartsWith(CountryCode, StringComparison.Ordinal))
            return "Enter a Ugandan number: 07XX XXX XXX or 2567XX XXX XXX.";
        return Operator(n) == null ? "That number is not on a network mobile money can charge." : null;
    }

    /// <summary>0772 123 456 — how a number is shown back to a person. Masked when asked, for logs
    /// and for a screen that only needs to recognise the number.</summary>
    public static string Display(string? phoneNumber, bool masked = false)
    {
        var n = Normalize(phoneNumber);
        if (n.Length != 12) return phoneNumber ?? string.Empty;
        var local = "0" + n[3..];
        return masked
            ? $"{local[..4]} ••• {local[^3..]}"
            : $"{local[..4]} {local[4..7]} {local[7..]}";
    }
}
