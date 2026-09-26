using QMgr.Application;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Calendar;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Services;

namespace QMgr.API.Application.Services;

public enum SchoolEventNoticeKind { Added, Changed, Cancelled }

/// <summary>
/// TELLING AN EVENT'S AUDIENCE (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E5, 2026-09-26). Until this existed a new
/// event, a moved one and a cancelled one told nobody: the calendar controller had no notification of any kind.
///
/// <list type="bullet">
/// <item><b>Exactly once.</b> A save bumps <see cref="SchoolEvent.Version"/> on a material change; a notice CLAIMS the
/// version with one conditional UPDATE before anything is sent — the staff-notice fan-out's rule. A retried request, a
/// double save or two keepers saving together cannot tell anybody twice.</item>
/// <item><b>Only its audience</b>, through <see cref="StaffAudience"/> — never "every member of staff" by default.</item>
/// <item><b>Never about the past.</b> An event whose last day has gone tells nobody; correcting last week's record is
/// housekeeping, not news. A cancellation of something still to come is the exception that matters most.</item>
/// <item><b>Not the person who did it.</b> They know.</item>
/// </list>
/// Sending is swallow-and-log per person (<c>NotifyManyAsync</c>), and runs AFTER the event is committed: a notice that
/// could not be written is a degraded success, never a reason to report the save as failed.
/// </summary>
public interface ISchoolEventNotifier
{
    Task<int> NotifyAsync(Guid eventId, SchoolEventNoticeKind kind, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>A reminder for one stage of the ladder. The caller has already claimed the stage.</summary>
    Task<int> RemindAsync(SchoolEvent e, string whenText, CancellationToken ct = default);
}

public class SchoolEventNotifier : ISchoolEventNotifier
{
    private readonly QMgrDbContext _db;
    private readonly INotificationService _notifications;
    private readonly IStaffPerformancePolicyService _policy;
    private readonly ILogger<SchoolEventNotifier> _logger;

    public SchoolEventNotifier(QMgrDbContext db, INotificationService notifications, IStaffPerformancePolicyService policy, ILogger<SchoolEventNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _policy = policy;
        _logger = logger;
    }

    public async Task<int> NotifyAsync(Guid eventId, SchoolEventNoticeKind kind, Guid? actorUserId, CancellationToken ct = default)
    {
        try
        {
            var e = await _db.SchoolEvents.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == eventId && x.IsActive, ct);
            if (e == null || (e.Audience & EventAudience.Staff) != EventAudience.Staff) return 0;

            var zone = await ZoneAsync(e, ct);
            if (e.EndsOn < BranchClock.Today(zone)) return 0;

            // THE CLAIM. Schema-qualified: raw SQL does not inherit the model's default schema.
            var version = e.Version;
            var claimed = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE qmgr.\"SchoolEvents\" SET \"NotifiedVersion\" = {version} WHERE \"Id\" = {e.Id} AND \"NotifiedVersion\" < {version}", ct);
            if (claimed == 0) return 0;

            var members = await StaffAudience.MembersAsync(_db, _policy, e.OrganizationId, e.BranchId, ct);
            var recipients = StaffAudience.RecipientsOf(e, members).Where(id => id != actorUserId).ToList();
            if (recipients.Count == 0) return 0;

            var (key, title, priority, icon) = kind switch
            {
                SchoolEventNoticeKind.Cancelled => (NotificationEventKeys.CalendarEventCancelled, $"Cancelled: {e.Title}", NotificationPriority.High, "calendar-x"),
                SchoolEventNoticeKind.Changed => (NotificationEventKeys.CalendarEventChanged, $"Changed: {e.Title}", NotificationPriority.Normal, "calendar-event"),
                _ => (NotificationEventKeys.CalendarEventAdded, $"New on the calendar: {e.Title}", NotificationPriority.Normal, "calendar-plus")
            };
            var message = kind == SchoolEventNoticeKind.Cancelled && !string.IsNullOrWhiteSpace(e.CancelReason)
                ? $"{SchoolEventText.When(e)}. {e.CancelReason}"
                : SchoolEventText.WhenAndWhere(e) + (e.AttendanceRequired ? " · attendance required" : "");

            var sent = await _notifications.NotifyManyAsync(recipients, Template(e, key, title, message, priority, icon), ct);
            _logger.LogInformation("Event {EventId} v{Version} ({Kind}): told {Sent}/{Total}", e.Id, version, kind, sent, recipients.Count);
            return sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event {EventId}: the {Kind} notice could not be sent", eventId, kind);
            return 0;
        }
    }

    public async Task<int> RemindAsync(SchoolEvent e, string whenText, CancellationToken ct = default)
    {
        var members = await StaffAudience.MembersAsync(_db, _policy, e.OrganizationId, e.BranchId, ct);
        var recipients = StaffAudience.RecipientsOf(e, members);
        if (recipients.Count == 0) return 0;
        return await _notifications.NotifyManyAsync(recipients,
            Template(e, NotificationEventKeys.CalendarEventReminder, $"{whenText}: {e.Title}", SchoolEventText.WhenAndWhere(e),
                e.AttendanceRequired ? NotificationPriority.High : NotificationPriority.Normal, "alarm"), ct);
    }

    private static CreateNotificationRequest Template(SchoolEvent e, string key, string title, string message, NotificationPriority priority, string icon) => new()
    {
        OrganizationId = e.OrganizationId,
        BranchId = e.BranchId,
        Title = title.Length > 200 ? title[..199] + "…" : title,
        Message = message,
        Type = NotificationType.Calendar,
        Priority = priority,
        // The recipient's preferences narrow these; the bell is always kept.
        Channels = NotificationChannel.InApp | NotificationChannel.Email | NotificationChannel.Push,
        EventKey = key,
        ActionUrl = SchoolEventText.Link(e),
        IconClass = icon,
        MetaData = new Dictionary<string, object> { ["eventId"] = e.Id }
    };

    private async Task<TimeZoneInfo> ZoneAsync(SchoolEvent e, CancellationToken ct)
    {
        var tz = await _db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == e.OrganizationId && b.IsActive && (e.BranchId == null || b.Id == e.BranchId))
            .OrderBy(b => b.CreatedAt).Select(b => b.Timezone).FirstOrDefaultAsync(ct);
        return BranchClock.Resolve(tz);
    }
}

/// <summary>How an event is written in a notice. Invariant, so the server says "Sep" whatever its own culture is.</summary>
public static class SchoolEventText
{
    public static string When(SchoolEvent e)
    {
        var day = e.StartsOn.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture);
        if (e.EndsOn > e.StartsOn) day += " – " + e.EndsOn.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture);
        if (e.StartTime is { } st)
            day += ", " + st.ToString("HH:mm", CultureInfo.InvariantCulture) + (e.EndTime is { } et ? "–" + et.ToString("HH:mm", CultureInfo.InvariantCulture) : " onwards");
        return day;
    }

    public static string WhenAndWhere(SchoolEvent e)
        => string.IsNullOrWhiteSpace(e.Location) ? When(e) : $"{When(e)} · {e.Location}";

    /// <summary>The calendar opened on the event's day with the event open.</summary>
    public static string Link(SchoolEvent e)
        => $"/calendar?date={e.StartsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&event={e.Id}";
}
