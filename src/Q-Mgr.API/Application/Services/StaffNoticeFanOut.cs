using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Staff;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;

namespace QMgr.API.Application.Services;

/// <summary>
/// Who a staff notice reaches, and the one place a notice is turned into per-recipient
/// Notification rows. Shared by StaffNoticesController (publish now) and
/// StaffPerformanceJobs.PublishScheduledNoticesAsync (publish when PublishAt arrives) so the two
/// cannot disagree about the audience or the shape of the notification. Idempotent on
/// <c>StaffNotice.NotificationsSentAt</c>: a notice already fanned out is never fanned out again.
///
/// The audience rule (plan §8), every clause AND-ed, null meaning "no restriction":
///   BranchId            null, or the user's branch (a user with no branch belongs to every branch)
///   AudienceDepartmentIds  null/empty, or intersects the user's DepartmentIds
///   AudienceRoleCodes      null/empty, or contains the user's role code
///   AudienceStaffGroup     null (everybody), or matches the group on the person's ROLE row
/// The reader-side filter ("GET notices — mine") applies exactly this predicate through
/// <see cref="IsRecipient"/>, so what a person sees and what they were told about is one rule.
/// </summary>
public static class StaffNoticeFanOut
{
    /// <summary>The MetaData key carried on every fan-out notification, so acknowledgement reporting can match rows to their notice without guessing from title text.</summary>
    public const string NoticeIdMetaKey = "noticeId";

    public static bool IsRecipient(StaffNotice notice, Guid? userBranchId, string? roleCode, Guid[]? departmentIds, string? staffGroup = null)
    {
        if (notice.BranchId.HasValue && userBranchId.HasValue && userBranchId.Value != notice.BranchId.Value) return false;

        if (notice.AudienceDepartmentIds is { Length: > 0 } depts)
        {
            if (departmentIds == null || !departmentIds.Any(depts.Contains)) return false;
        }

        if (notice.AudienceRoleCodes is { Length: > 0 } roles)
        {
            if (roleCode == null || !roles.Any(r => string.Equals(r, roleCode, StringComparison.OrdinalIgnoreCase))) return false;
        }

        // A notice may name one staff group; null means everybody. Matched by NAME through the one
        // home, StaffGroups.Applies — the role code no longer decides, because it never could (it
        // made every custom role, and admin, manager and viewer, teaching staff).
        if (!StaffGroups.Applies(notice.AudienceStaffGroup, staffGroup)) return false;

        return true;
    }

    /// <summary>Every active staff member of the organization the notice is addressed to.</summary>
    public static async Task<List<(Guid UserId, string FullName)>> ResolveRecipientsAsync(QMgrDbContext db, StaffNotice notice, CancellationToken ct = default)
    {
        var candidates = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == notice.OrganizationId && u.IsActive && u.Role.Code != RoleCodes.SuperAdmin)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username, u.AssignedBranchId, RoleCode = u.Role.Code, StaffGroup = u.Role.StaffGroup, u.DepartmentIds })
            .ToListAsync(ct);

        return candidates
            .Where(u => IsRecipient(notice, u.AssignedBranchId, u.RoleCode, u.DepartmentIds, u.StaffGroup))
            .Select(u => (u.Id, PersonNames.Display(notice.OrganizationId, u.FirstName, u.LastName, u.Username)))
            .ToList();
    }

    /// <summary>
    /// Creates one Notification per recipient and stamps NotificationsSentAt. Returns the number
    /// sent, or 0 when the notice was already fanned out, is inactive, or is not yet due. Sends are
    /// individually swallow-and-logged: one unreachable mailbox must not stop the other fifty, and
    /// the stamp is written regardless so a partial failure does not re-notify everyone next pass
    /// (the AppointmentJobs rationale). Call AFTER the notice row is committed.
    /// </summary>
    public static async Task<int> FanOutAsync(QMgrDbContext db, INotificationService notifications, StaffNotice notice, ILogger logger, CancellationToken ct = default)
    {
        if (!notice.IsActive || notice.NotificationsSentAt.HasValue || notice.PublishAt > DateTime.UtcNow) return 0;

        // CONCURRENCY (found by the e2e's race sections, 2026-09-16): the controller fans out when a
        // notice becomes due on save, and the 15-minute job fans out whatever is due and unsent.
        // Both read NotificationsSentAt == null and both would notify every recipient. The CLAIM is
        // one conditional UPDATE: exactly one caller changes the row, the other sees 0 rows and
        // stops. Stamped before sending, which is the rule this method already had (a partial
        // failure must not re-notify everyone); schema-qualified because raw SQL does not inherit
        // the model's default schema.
        var claimedAt = DateTime.UtcNow;
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE qmgr.\"StaffNotices\" SET \"NotificationsSentAt\" = {claimedAt} WHERE \"Id\" = {notice.Id} AND \"NotificationsSentAt\" IS NULL", ct);
        if (claimed == 0) return 0;
        notice.NotificationsSentAt = claimedAt;

        var recipients = await ResolveRecipientsAsync(db, notice, ct);
        var summary = Summary(notice.BodyHtml, 160);
        var sent = 0;

        foreach (var (userId, _) in recipients)
        {
            try
            {
                await notifications.CreateInAppNotificationAsync(new CreateNotificationRequest
                {
                    UserId = userId,
                    OrganizationId = notice.OrganizationId,
                    BranchId = notice.BranchId,
                    Title = notice.Title,
                    Message = summary,
                    Type = NotificationType.StaffPerformance,
                    Priority = notice.RequiresAcknowledgement ? NotificationPriority.High : NotificationPriority.Normal,
                    Channels = NotificationChannel.InApp | NotificationChannel.Email,
                    EventKey = NotificationEventKeys.StaffNoticePublished,
                    ActionUrl = "/portal",
                    IconClass = notice.IsPinned ? "pin-angle" : "megaphone",
                    MetaData = new Dictionary<string, object> { [NoticeIdMetaKey] = notice.Id },
                    ExpiresAt = notice.ExpiresAt
                }, ct);
                sent++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Staff notice {NoticeId}: could not notify user {UserId}", notice.Id, userId);
            }
        }

        logger.LogInformation("Staff notice {NoticeId} \"{Title}\" fanned out to {Sent}/{Total} recipient(s)", notice.Id, notice.Title, sent, recipients.Count);
        return sent;
    }

    /// <summary>Text-only body, whitespace collapsed, cut to <paramref name="max"/> characters, for the bell and the SMS.</summary>
    public static string Summary(string? html, int max)
    {
        var text = StaffNoticeHtml.ToPlainText(html);
        if (text.Length <= max) return text;
        return text[..(max - 1)].TrimEnd() + "…";
    }
}

/// <summary>
/// The sanitiser for a notice body. DocsController stores DocArticle.BodyHtml with only the signed
/// upload-token strip (UploadLinks.StripAll) — there was no HTML sanitiser anywhere in the API
/// before this — so this is the small allow-list the plan asked for: known block and inline tags
/// keep a handful of safe attributes; everything else loses its tag and keeps its text; scripts,
/// styles, frames and embeds lose their content too; event handlers and javascript:/data: URLs are
/// dropped. Regex-based on purpose — no HTML parsing dependency on the server — and conservative:
/// when in doubt a tag is removed, not kept.
/// </summary>
public static class StaffNoticeHtml
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "strong", "b", "em", "i", "u", "s", "sub", "sup", "mark", "small",
        "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
        "a", "img", "span", "div", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption"
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "alt", "title", "colspan", "rowspan", "target", "rel"
    };

    private static readonly Regex DangerousBlocks = new(@"<(script|style|iframe|object|embed|noscript|template|svg|math|form|input|button|select|textarea)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex Attributes = new(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var s = Comments.Replace(html, string.Empty);
        s = DangerousBlocks.Replace(s, string.Empty);

        s = Tags.Replace(s, m =>
        {
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value;
            if (!Allowed.Contains(name)) return string.Empty;
            if (closing) return $"</{name.ToLowerInvariant()}>";

            var attrs = new List<string>();
            foreach (Match a in Attributes.Matches(m.Groups[3].Value))
            {
                var attrName = a.Groups[1].Value;
                if (!AllowedAttributes.Contains(attrName)) continue; // drops every on* handler and style
                var value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                if (attrName.Equals("href", StringComparison.OrdinalIgnoreCase) || attrName.Equals("src", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsSafeUrl(value)) continue;
                }
                attrs.Add($"{attrName.ToLowerInvariant()}=\"{WebUtility.HtmlEncode(WebUtility.HtmlDecode(value))}\"");
            }

            // Links open in a new tab and never hand the notice page's window to the target.
            if (name.Equals("a", StringComparison.OrdinalIgnoreCase) && attrs.Any(x => x.StartsWith("href=")))
            {
                attrs.RemoveAll(x => x.StartsWith("target=") || x.StartsWith("rel="));
                attrs.Add("target=\"_blank\"");
                attrs.Add("rel=\"noopener noreferrer\"");
            }

            var selfClosing = name.Equals("br", StringComparison.OrdinalIgnoreCase) || name.Equals("hr", StringComparison.OrdinalIgnoreCase) || name.Equals("img", StringComparison.OrdinalIgnoreCase);
            return $"<{name.ToLowerInvariant()}{(attrs.Count > 0 ? " " + string.Join(" ", attrs) : "")}{(selfClosing ? " /" : "")}>";
        });

        return s.Trim();
    }

    private static bool IsSafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = WebUtility.HtmlDecode(value).Trim();
        // Strip control characters and whitespace an attacker uses to split "java\tscript:".
        var compact = new string(v.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        if (compact.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
        if (compact.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)) return false;
        if (compact.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        return v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
               || v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
               || v.StartsWith("/")
               || v.StartsWith("#");
    }

    /// <summary>Tags removed, entities decoded, whitespace collapsed. Block boundaries become a space so two paragraphs do not run together.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var s = DangerousBlocks.Replace(html, string.Empty);
        s = Regex.Replace(s, @"<(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", " ", RegexOptions.IgnoreCase);
        s = Tags.Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s);
        return Whitespace.Replace(s, " ").Trim();
    }
}
