using QMgr.Application.Branding;
using System.Globalization;
using System.Text;

namespace QMgr.API.Application.Services;

/// <summary>
/// A hand-written RFC 5545 (iCalendar) writer for the personal calendar feed (plan TERM_PROGRAMME_CALENDAR_AND_GATES
/// §9, 2026-09-23). No NuGet: the standing no-third-party-dependencies rule, and the part of the format a PUBLISH
/// feed needs is small.
///
/// The three rules that are easy to get wrong, each cited:
/// <list type="bullet">
/// <item>Lines end in CRLF (§3.1), and a content line longer than 75 OCTETS is folded — CRLF plus one space — never
/// splitting a UTF-8 sequence. Counting characters instead of bytes breaks on the first "Nyakato Ainembabazi's"
/// with a curly apostrophe.</item>
/// <item>TEXT values escape backslash, semicolon, comma and newline (§3.3.11). CATEGORIES is a comma-separated
/// list, so a comma INSIDE one category must be escaped too.</item>
/// <item>An all-day event is <c>VALUE=DATE</c> and its DTEND is EXCLUSIVE (§3.6.1): a one-day event on the 23rd
/// ends on the 24th. Writing the inclusive last day makes every calendar show it one day short.</item>
/// </list>
/// A timed value is always written in UTC (<c>…Z</c>, §3.3.5 form #2), so no VTIMEZONE block is needed.
/// </summary>
public sealed class IcsWriter
{
    private const string Crlf = "\r\n";
    private const int MaxOctets = 75;
    private readonly StringBuilder _sb = new();

    public IcsWriter(string calendarName, string prodId = ProductBrand.CalendarProductId)
    {
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:" + prodId);
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:" + Escape(calendarName));
    }

    /// <summary>An all-day event: <paramref name="lastDay"/> is INCLUSIVE here and written exclusive.</summary>
    public void AllDayEvent(string uid, DateTime stampUtc, DateOnly firstDay, DateOnly lastDay,
        string summary, string? location, string? description, string? category, DateTime? lastModifiedUtc = null,
        int sequence = 0, bool cancelled = false)
    {
        Begin(uid, stampUtc, lastModifiedUtc, sequence, cancelled);
        Line("DTSTART;VALUE=DATE:" + Date(firstDay));
        Line("DTEND;VALUE=DATE:" + Date((lastDay < firstDay ? firstDay : lastDay).AddDays(1)));
        Body(summary, location, description, category);
    }

    /// <summary>A timed event. <paramref name="endUtc"/> null = no stated end ("onwards").</summary>
    public void TimedEvent(string uid, DateTime stampUtc, DateTime startUtc, DateTime? endUtc,
        string summary, string? location, string? description, string? category, DateTime? lastModifiedUtc = null,
        int sequence = 0, bool cancelled = false)
    {
        Begin(uid, stampUtc, lastModifiedUtc, sequence, cancelled);
        Line("DTSTART:" + Utc(startUtc));
        if (endUtc is { } end && end > startUtc) Line("DTEND:" + Utc(end));
        Body(summary, location, description, category);
    }

    /// <summary>Closes the calendar and returns the whole document.</summary>
    public string Finish()
    {
        Line("END:VCALENDAR");
        return _sb.ToString();
    }

    /// <summary>
    /// SEQUENCE is the event's revision (RFC 5546 §2.1.4: bumped when the date, time or status changes), and a cancelled
    /// event is sent as STATUS:CANCELLED rather than dropped (§3.2.5) — a calendar that already has it strikes it through
    /// instead of silently keeping the old entry. DTSTAMP is the moment the event last changed, not the moment of the
    /// fetch: a stamp that moved on every poll told every client the whole calendar had changed every five minutes.
    /// </summary>
    private void Begin(string uid, DateTime stampUtc, DateTime? lastModifiedUtc, int sequence, bool cancelled)
    {
        Line("BEGIN:VEVENT");
        Line("UID:" + Escape(uid));
        Line("DTSTAMP:" + Utc(stampUtc));
        if (lastModifiedUtc is { } lm) Line("LAST-MODIFIED:" + Utc(lm));
        if (sequence > 0) Line("SEQUENCE:" + sequence.ToString(CultureInfo.InvariantCulture));
        if (cancelled) Line("STATUS:CANCELLED");
    }

    private void Body(string summary, string? location, string? description, string? category)
    {
        Line("SUMMARY:" + Escape(summary));
        if (!string.IsNullOrWhiteSpace(location)) Line("LOCATION:" + Escape(location));
        if (!string.IsNullOrWhiteSpace(description)) Line("DESCRIPTION:" + Escape(description));
        if (!string.IsNullOrWhiteSpace(category)) Line("CATEGORIES:" + Escape(category));
        Line("TRANSP:TRANSPARENT");
        Line("END:VEVENT");
    }

    private static string Date(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Utc(DateTime t)
    {
        var utc = t.Kind switch
        {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc) // this codebase stores instants as UTC
        };
        return utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>RFC 5545 §3.3.11 TEXT escaping.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ';': sb.Append("\\;"); break;
                case ',': sb.Append("\\,"); break;
                case '\r':
                    if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                    sb.Append("\\n");
                    break;
                case '\n': sb.Append("\\n"); break;
                default:
                    // Other control characters are not allowed in a TEXT value (§3.3.11 ESAFE-CHAR excludes CONTROL).
                    if (c < 0x20 && c != '\t') continue;
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Appends one content line, folded at 75 octets without splitting a UTF-8 sequence (§3.1).</summary>
    private void Line(string content)
    {
        var budget = MaxOctets;
        var used = 0;
        var i = 0;
        while (i < content.Length)
        {
            // One Unicode scalar at a time, so a surrogate pair is never split across a fold.
            var len = char.IsHighSurrogate(content[i]) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]) ? 2 : 1;
            var octets = Encoding.UTF8.GetByteCount(content.AsSpan(i, len));
            if (used + octets > budget)
            {
                _sb.Append(Crlf).Append(' ');
                budget = MaxOctets - 1; // the leading space counts towards the 75
                used = 0;
            }
            _sb.Append(content, i, len);
            used += octets;
            i += len;
        }
        _sb.Append(Crlf);
    }
}
