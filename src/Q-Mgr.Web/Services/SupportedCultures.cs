using System.Globalization;

namespace QMgr.Web.Services;

/// <summary>
/// The languages the customer-facing screens are offered in. English is the fallback for anything
/// untranslated, and because resource keys are the English text itself, a missing translation
/// degrades to readable English rather than to a key name.
/// <para>
/// Swahili and Luganda are here because the kiosk, display and feedback screens are read by the
/// public in Uganda; add a culture by adding a row here and a matching
/// <c>SharedResources.&lt;culture&gt;.resx</c>, with no other code change.
/// </para>
/// </summary>
public static class SupportedCultures
{
    public const string CookieName = ".QMgr.Culture";

    public static readonly IReadOnlyList<(string Code, string EnglishName, string NativeName)> All = new[]
    {
        ("en", "English", "English"),
        ("sw", "Swahili", "Kiswahili"),
        ("lg", "Luganda", "Luganda"),
    };

    public static string[] Codes => All.Select(c => c.Code).ToArray();

    /// <summary>
    /// Builds the CultureInfo list for request localization. A culture the host's ICU data does not
    /// know is skipped rather than thrown, so an unusual deployment cannot fail to start over a
    /// language pack.
    /// </summary>
    public static List<CultureInfo> ToCultureInfos()
    {
        var cultures = new List<CultureInfo>();
        foreach (var (code, _, _) in All)
        {
            try
            {
                cultures.Add(new CultureInfo(code));
            }
            catch (CultureNotFoundException)
            {
                // Not available on this host; English remains the fallback.
            }
        }

        if (cultures.Count == 0) cultures.Add(new CultureInfo("en"));
        return cultures;
    }

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        All.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    public static string NativeNameOf(string code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)).NativeName ?? code;
}
