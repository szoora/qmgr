using QMgr.Application.Branding;
using System.Globalization;
using System.Net;
using System.Text;
using QMgr.Application.DTOs;
using QMgr.Infrastructure.Email;

namespace QMgr.API.Application.Services;

/// <summary>
/// Renders visitor reports as email HTML.
///
/// Everything is inlined: table styles as style attributes, no external CSS, no attachments. Mail
/// clients strip stylesheets, and NotificationAttachment addresses a file already sitting in media
/// storage — so attaching anything here would mean writing a file into permanent storage on every
/// scheduled send, accumulating one per report per day forever. A person reading a 06:00 exception
/// digest on a phone wants the rows on screen; the full log stays one click away in the app.
///
/// Long tables are truncated with an explicit count of what was left out, because a silently
/// shortened list reads as "that is all of them" and is worse than no list.
/// </summary>
public static class VisitorReportEmail
{
    private const int MaxRows = 40;

    // The palette and the four building blocks were promoted into EmailTemplates on 2026-09-16 so the
    // staff digests share them. These thin aliases keep this file's render methods readable; the
    // rule itself has one copy, over there.
    private const string Ink = EmailTemplates.ReportInk;
    private const string Muted = EmailTemplates.ReportMuted;
    private const string Danger = EmailTemplates.ReportDanger;

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Shell(string title, string subtitle, string body)
        => EmailTemplates.ReportShell(title, subtitle, body, "Sent by " + ProductBrand.Name + " Visitor Management. Times are shown in the branch's local timezone.");

    private static string Table(string[] headers, IEnumerable<string[]> rows, int totalCount)
        => EmailTemplates.ReportTable(headers, rows, totalCount);

    private static string Section(string heading) => EmailTemplates.ReportSection(heading);

    private static string Stat(string label, string value, bool alert = false) => EmailTemplates.ReportStat(label, value, alert);

    // -----------------------------------------------------------------------------------------

    public static string RenderEvacuation(EvacuationReportDto report)
    {
        var rows = report.People.Select(p => new[]
        {
            $"<strong>{E(p.FullName)}</strong>",
            E(p.BadgeCode),
            E(p.Company),
            E(p.HostName),
            E(p.Phone),
            p.CheckedInAt.HasValue ? E(p.CheckedInAt.Value.ToLocalTime().ToString("HH:mm")) : ""
        });

        var body = new StringBuilder();
        body.Append(Stat("Total on site", report.TotalOnSite.ToString(), alert: report.TotalOnSite > 0));
        body.Append(Stat("Named visitors", report.CheckedInVisitorCount.ToString()));
        body.Append(Stat("Under group passes", report.GroupPassOccupantCount.ToString()));

        body.Append(Section("Named visitors on site"));
        body.Append(Table(new[] { "Name", "Badge", "Company", "Host", "Phone", "In" }, rows, report.People.Count));

        if (report.GroupPasses.Count > 0)
        {
            body.Append(Section("Group passes — headcount only, no names recorded"));
            body.Append(Table(
                new[] { "Pass", "People" },
                report.GroupPasses.Select(g => new[] { E(g.Label), g.OccupantCount.ToString() }),
                report.GroupPasses.Count));
        }

        // The students caveat travels with the report, always. On a roll call at an assembly point,
        // a missing caveat is more dangerous than a missing column — a marshal who assumes the
        // headcount covers the school roll stops counting children.
        body.Append($@"<p style=""margin:14px 0 0;padding:10px 12px;background:#f8e6e4;border-left:3px solid {Danger};color:{Ink};font-size:13px"">{E(report.StudentsNote)}</p>");

        return Shell(
            $"Evacuation roll call — {report.BranchName}",
            string.Create(CultureInfo.InvariantCulture, $"Generated {report.GeneratedAt.ToLocalTime():dddd d MMMM yyyy, HH:mm}"),
            body.ToString());
    }

    public static string RenderSummary(VisitorReportDtoV2 report)
    {
        var body = new StringBuilder();
        body.Append(Stat("Total visits", report.TotalVisits.ToString("N0")));
        body.Append(Stat("Unique visitors", report.UniqueVisitors.ToString("N0")));
        body.Append(Stat("Avg dwell", report.DwellDenominator > 0 ? $"{report.AvgDwellMinutes:0} min" : "—"));
        body.Append(Stat("Consent", report.ConsentDenominator > 0 ? $"{report.ConsentCompliancePercent:0.#}%" : "—"));
        body.Append(Stat("Watchlisted", report.WatchlistIncidents.ToString("N0"), alert: report.WatchlistIncidents > 0));

        body.Append(Section("Busiest days"));
        body.Append(Table(
            new[] { "Day", "Visits" },
            report.VisitsByDay.OrderByDescending(d => d.Count).Take(7)
                .Select(d => new[] { E(d.Day.ToString("ddd d MMM", CultureInfo.InvariantCulture)), d.Count.ToString("N0") }),
            Math.Min(7, report.VisitsByDay.Count)));

        if (report.TopHosts.Count > 0)
        {
            body.Append(Section("Most-visited hosts"));
            body.Append(Table(
                new[] { "Host", "Visits" },
                report.TopHosts.Select(h => new[] { E(h.HostName), h.Count.ToString("N0") }),
                report.TopHosts.Count));
        }

        if (report.PreRegistration.Booked > 0)
        {
            body.Append(Section("Pre-registration"));
            body.Append(Stat("Booked", report.PreRegistration.Booked.ToString()));
            body.Append(Stat("Arrived", report.PreRegistration.Arrived.ToString()));
            body.Append(Stat("No-show", report.PreRegistration.NoShow.ToString(), alert: report.PreRegistration.NoShow > 0));
            body.Append(Stat("No-show rate", $"{report.PreRegistration.NoShowRatePercent:0.#}%"));
        }

        return Shell(
            $"Visitor summary — {report.ScopeName}",
            string.Create(CultureInfo.InvariantCulture, $"{report.From:d MMM yyyy} to {report.To:d MMM yyyy} · {report.FilterDescription} · times in {report.TimeZoneId}"),
            body.ToString());
    }

    public static string RenderExceptions(VisitorExceptionsDto exceptions)
    {
        var body = new StringBuilder();

        if (exceptions.TotalCount == 0)
        {
            body.Append($@"<p style=""margin:0;padding:14px 16px;background:#e4f0ea;border-left:3px solid #2c6f4c;font-size:14px"">Nothing needs attention. Everyone who checked in has checked out, every visit has a host, and no contractor was admitted on a lapsed induction.</p>");
            return Shell($"Visitor exceptions — {exceptions.ScopeName}", string.Create(CultureInfo.InvariantCulture, $"{exceptions.From:d MMM} to {exceptions.To:d MMM yyyy}"), body.ToString());
        }

        body.Append(Stat("Still on site", exceptions.StillOnSite.Count.ToString(), alert: exceptions.StillOnSite.Count > 0));
        body.Append(Stat("Overstayed", exceptions.Overstayed.Count.ToString(), alert: exceptions.Overstayed.Count > 0));
        body.Append(Stat("No host", exceptions.NoHostRecorded.Count.ToString(), alert: exceptions.NoHostRecorded.Count > 0));
        body.Append(Stat("Induction", exceptions.InductionLapsed.Count.ToString(), alert: exceptions.InductionLapsed.Count > 0));

        void Add(string heading, List<VisitorExceptionDto> rows)
        {
            if (rows.Count == 0) return;
            body.Append(Section(heading));
            body.Append(Table(
                new[] { "Name", "Badge", "Detail" },
                rows.Take(MaxRows).Select(r => new[]
                {
                    $"<strong>{E(r.FullName)}</strong>{(r.IsWatchlisted ? $@" <span style=""color:{Danger}"">flagged</span>" : "")}",
                    E(r.BadgeCode),
                    E(r.Detail)
                }),
                rows.Count));
        }

        Add("Checked in on an earlier day and never checked out", exceptions.StillOnSite);
        Add($"On site longer than {Plural(exceptions.OverstayThresholdHours, "hour")}", exceptions.Overstayed);
        Add("Admitted with no host recorded", exceptions.NoHostRecorded);
        Add("Contractors admitted without a valid induction", exceptions.InductionLapsed);
        Add($"Visited {Plural(exceptions.FrequentVisitorThreshold, "time")} or more", exceptions.FrequentVisitors);

        return Shell(
            $"Visitor exceptions — {exceptions.ScopeName}",
            string.Create(CultureInfo.InvariantCulture, $"{exceptions.From:d MMM} to {exceptions.To:d MMM yyyy} · open visits are shown regardless of that range"),
            body.ToString());
    }

    public static string RenderCompliance(VisitorComplianceDto compliance)
    {
        var body = new StringBuilder();
        body.Append(Stat("Contractor visits", compliance.ContractorVisits.ToString()));
        body.Append(Stat("Induction OK", $"{compliance.InductionCompliancePercent:0.#}%",
            alert: compliance.ContractorVisits > 0 && compliance.InductionCompliancePercent < 100));
        body.Append(compliance.ConsentRequired
            ? Stat("Consent captured", $"{compliance.ConsentCompliancePercent:0.#}%")
            : Stat("Consent", "Not required"));

        if (compliance.WatchlistActivity.Count > 0)
        {
            body.Append(Section("Watchlist activity"));
            body.Append(Table(
                new[] { "Name", "Event", "Reason" },
                compliance.WatchlistActivity.Take(MaxRows).Select(w => new[]
                {
                    $"<strong>{E(w.FullName)}</strong>",
                    E(w.Event),
                    E(w.OverrideReason ?? w.Reason)
                }),
                compliance.WatchlistActivity.Count));
        }

        if (compliance.InductionRegister.Any(i => !i.ValidOnVisitDate))
        {
            body.Append(Section("Contractors admitted without a valid induction"));
            body.Append(Table(
                new[] { "Name", "Company", "Induction" },
                compliance.InductionRegister.Where(i => !i.ValidOnVisitDate).Take(MaxRows).Select(i => new[]
                {
                    $"<strong>{E(i.FullName)}</strong>",
                    E(i.Company),
                    i.InductionCompletedAt.HasValue
                        ? E(string.Create(CultureInfo.InvariantCulture, $"expired {i.InductionExpiresAt:d MMM yyyy}"))
                        : "none on record"
                }),
                compliance.InductionRegister.Count(i => !i.ValidOnVisitDate)));
        }

        body.Append(Section("Data retention"));
        var r = compliance.Retention;
        body.Append($@"<p style=""margin:0 0 18px;font-size:13px;color:{Muted}"">
            Policy: keep visitor records for {r.RetentionDays} days.
            {(r.LastRunAt.HasValue
                ? E(string.Create(CultureInfo.InvariantCulture, $"Last purge ran {r.LastRunAt.Value.ToLocalTime():d MMM yyyy, HH:mm}, removing {r.LastRunVisitsPurged} visit(s) and {r.LastRunProfilesPurged} profile(s). {r.TotalVisitsPurged} visits removed under this policy in total."))
                : "The purge has not recorded a run yet.")}
        </p>");

        return Shell(
            $"Visitor compliance — {compliance.ScopeName}",
            string.Create(CultureInfo.InvariantCulture, $"{compliance.From:d MMM yyyy} to {compliance.To:d MMM yyyy}"),
            body.ToString());
    }

    public static string RenderVisitorLog(VisitorReportDtoV2 report, string csv)
    {
        // The log is genuinely a file, and this is the one report an inline table serves badly.
        // Rather than write a CSV into permanent media storage on every send, the email carries the
        // headline count and says plainly where the full download lives.
        var lineCount = Math.Max(0, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);

        var body = new StringBuilder();
        body.Append(Stat("Visits logged", lineCount.ToString("N0")));
        body.Append(Stat("Unique visitors", report.UniqueVisitors.ToString("N0")));
        body.Append($@"<p style=""margin:14px 0 0;font-size:13px"">
            The full row-per-visit register for this period is available from
            <strong>Reports &rsaquo; Visitors</strong> in the app, where it downloads as CSV or Excel.
            It is not attached here because it carries names, phone numbers and email addresses for
            every visitor in the period, and email is the wrong place to scatter that.
        </p>");

        return Shell(
            $"Visitor log — {report.ScopeName}",
            string.Create(CultureInfo.InvariantCulture, $"{report.From:d MMM yyyy} to {report.To:d MMM yyyy} · {report.FilterDescription}"),
            body.ToString());
    }
}
