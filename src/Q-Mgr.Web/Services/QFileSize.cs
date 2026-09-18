using System.Globalization;

namespace QMgr.Web.Services;

/// <summary>
/// How a file size is written in this product. One place, for the same reason
/// <see cref="QDateFormat"/> exists: there were five hand-written copies and they disagreed.
///
/// <para>
/// 512 bytes read as <c>512 B</c> in the Library picker and <c>0.5 KB</c> in the Media Library;
/// 1 MB as <c>1 MB</c> in one and <c>1.0 MB</c> in the other; a null size as <c>--</c>, <c>—</c>
/// or <c>0 B</c> depending on which page you were on. None of that is a bug a user reports, but
/// two adjacent pages naming the same file two different sizes is the same drift the date audit
/// found, so it gets the same treatment.
/// </para>
///
/// <para>
/// <b>The shape: whole bytes below 1 KB, then at most one decimal, binary steps (1024).</b>
/// <c>512 B</c>, <c>1.5 KB</c>, <c>1 MB</c>, <c>24.3 MB</c>. Whole bytes below a kilobyte because
/// "0.5 KB" is a rounded figure where the exact one is shorter; a trailing <c>.0</c> is dropped
/// because a file list is scanned, not read, and "1 MB" is what a person would say. Binary steps
/// and the plain B/KB/MB labels match what every file dialog the user already has shows.
/// InvariantCulture, so the screen, a CSV and a printed sheet cannot disagree on the separator.
/// </para>
///
/// <para>
/// Ready for the copies still in place elsewhere — <c>DocumentLibrary.FormatSize</c>,
/// <c>CampaignMarketing.FormatFileSize</c> (pass <c>whenUnknown: "0 B"</c> to keep its wording)
/// and <c>Billing/Overview.FormatBytes</c> (pass <c>decimals: 2</c> for a storage quota, where the
/// second place carries real money) — so each can be pointed here without changing what it shows.
/// </para>
/// </summary>
public static class QFileSize
{
    /// <summary>Binary steps, and no unit past TB: nothing this app stores gets there.</summary>
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// 512 B · 1.5 KB · 1 MB · 24.3 MB. <paramref name="decimals"/> is the MAXIMUM number of
    /// decimal places, never a minimum — a whole figure keeps no trailing zeros.
    /// </summary>
    public static string Format(long bytes, int decimals = 1)
    {
        if (bytes < 0) bytes = 0;

        // Below a kilobyte the exact count is both shorter and more honest than a rounded "0.5 KB".
        if (bytes < 1024) return $"{bytes} B";

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var pattern = decimals <= 0 ? "0" : "0." + new string('#', decimals);
        return string.Concat(size.ToString(pattern, Inv), " ", Units[unit]);
    }

    /// <summary>
    /// The same, for the many DTOs that carry a nullable size. A missing size is the em dash the
    /// rest of the app uses for "no value" (<see cref="QDateFormat"/>), not "0 B" — a file whose
    /// size was never recorded is not an empty file.
    /// </summary>
    public static string Format(long? bytes, int decimals = 1, string whenUnknown = "—")
        => bytes.HasValue ? Format(bytes.Value, decimals) : whenUnknown;
}
