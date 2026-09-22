using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Notification;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Decides which channels a given notification actually goes out on, for a given recipient.
///
/// Before this existed there was no such decision: <c>CreateInAppNotificationAsync</c> sent on
/// whatever channels the CALLER asked for, to whatever address the CALLER had looked up, and the
/// recipient had no say at all. That is fine for a queue ticket sent to a member of the public;
/// it is not fine for staff notifications, where "stop emailing me every time a child is late"
/// is the difference between a useful alert and a filter rule.
///
/// The precedence is: organization master switch → organization per-event default → the person's
/// own choice. The person wins where they have expressed one, and null everywhere means "follow
/// the default", which is the state every existing row is in.
///
/// The in-app bell is never switchable. It costs nothing, it is the record that the person was
/// told, and a notification nobody can find afterwards is worse than one nobody wanted.
///
/// THE ONE HOME for reading and writing <c>Users.NotificationPreferences</c>. Do not parse that
/// column anywhere else — a second copy of this rule is how the stored shape and the reading of it
/// drift apart, which is this codebase's most frequently repeated bug.
/// </summary>
public interface INotificationPreferenceResolver
{
    /// <summary>
    /// Narrows <paramref name="requested"/> to what this recipient actually wants and this
    /// organization actually has switched on. InApp always survives.
    ///
    /// <para><b>Push is now decided here rather than passed through</b> (2026-09-22, when the mobile
    /// app arrived). It can be ADDED as well as removed, because the caller of a notification does
    /// not know whether the recipient owns a handset and should not have to: the event's own default
    /// and the person's switch decide, and a person with no device is skipped harmlessly further
    /// down. That makes this the one method that can widen the requested set, which is why it is
    /// stated here.</para>
    /// </summary>
    Task<NotificationChannel> ResolveAsync(Guid? userId, Guid organizationId, string? eventKey,
        NotificationChannel requested, CancellationToken cancellationToken = default);

    Task<UserNotificationPreferencesDto> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SaveAsync(Guid userId, UserNotificationPreferencesDto preferences, CancellationToken cancellationToken = default);

    /// <summary>Parses the stored blob. Returns defaults for null/malformed rather than throwing — a corrupt preference blob must not stop a notification going out.</summary>
    static UserNotificationPreferencesDto Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new UserNotificationPreferencesDto();
        try
        {
            return JsonSerializer.Deserialize<UserNotificationPreferencesDto>(json) ?? new UserNotificationPreferencesDto();
        }
        catch (JsonException)
        {
            return new UserNotificationPreferencesDto();
        }
    }
}

public class NotificationPreferenceResolver : INotificationPreferenceResolver
{
    private readonly QMgrDbContext _context;
    private readonly ILogger<NotificationPreferenceResolver> _logger;

    public NotificationPreferenceResolver(QMgrDbContext context, ILogger<NotificationPreferenceResolver> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<NotificationChannel> ResolveAsync(
        Guid? userId, Guid organizationId, string? eventKey,
        NotificationChannel requested, CancellationToken cancellationToken = default)
    {
        // The bell is not negotiable, and a broadcast (no user) has nobody to hold a preference.
        var resolved = requested | NotificationChannel.InApp;
        if (userId == null) return resolved;

        // An event sent without a key keeps the pre-preferences behaviour: deliver on whatever the
        // caller asked for. Every welfare alert carries one; a queue ticket does not need to.
        if (!NotificationEventKeys.IsKnown(eventKey)) return resolved;

        var settings = await _context.NotificationSettings
            .AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => new { s.StaffNotifyEmail, s.StaffNotifySms, s.EmailEnabled, s.SmsEnabled })
            .FirstOrDefaultAsync(cancellationToken);

        // Two gates, and BOTH are checked here rather than left to the send:
        //   EmailEnabled/SmsEnabled  — does this tenant have the channel configured at all?
        //   StaffNotify*             — does it want staff notifications on that channel?
        //
        // Checking the channel switch here as well as in the sender is not redundant. Without it,
        // every notification for a tenant with email switched off enqueues a background job that
        // does nothing, and the delivery log fills with rows that LOOK like failures — the exact
        // skipped-vs-failed conflation this whole rework exists to end. Deciding it once, up
        // front, means the job is never queued and the log stays a record of real attempts.
        //
        // No settings row at all: the organization has never configured notifications, so nothing
        // can go out on those channels regardless of the defaults.
        var orgEmail = settings is { EmailEnabled: true, StaffNotifyEmail: true };
        var orgSms = settings is { SmsEnabled: true, StaffNotifySms: true };

        var raw = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.NotificationPreferences)
            .FirstOrDefaultAsync(cancellationToken);

        var prefs = INotificationPreferenceResolver.Parse(raw);
        var definition = NotificationEventKeys.All.First(e => e.Key == eventKey);
        var mine = prefs.Events.FirstOrDefault(e => e.EventKey == eventKey);

        var wantsEmail = orgEmail && prefs.EmailEnabled && (mine?.Email ?? definition.DefaultEmail);
        var wantsSms = orgSms && prefs.SmsEnabled && (mine?.Sms ?? definition.DefaultSms);

        // Push has no organization gate, and that is on purpose: unlike SMS it costs the tenant
        // nothing per message, and unlike email it needs no per-tenant relay to configure. The gates
        // that matter are the person's (here) and the handset's OS permission, which the server
        // cannot see and which UserDeviceSession.PushPermitted carries instead.
        var wantsPush = prefs.PushEnabled && (mine?.Push ?? definition.DefaultPush);

        if (!wantsEmail) resolved &= ~NotificationChannel.Email;
        if (!wantsSms) resolved &= ~NotificationChannel.Sms;
        if (!wantsPush) resolved &= ~NotificationChannel.Push;
        else resolved |= NotificationChannel.Push;

        return resolved;
    }

    public async Task<UserNotificationPreferencesDto> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var raw = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.NotificationPreferences)
            .FirstOrDefaultAsync(cancellationToken);

        var prefs = INotificationPreferenceResolver.Parse(raw);

        // Fill in every known event so the panel renders a complete list rather than only the rows
        // somebody happens to have touched — and so a new event category appears with its default
        // rather than silently absent.
        foreach (var def in NotificationEventKeys.All)
        {
            if (prefs.Events.All(e => e.EventKey != def.Key))
                prefs.Events.Add(new NotificationChannelPreference { EventKey = def.Key, Email = def.DefaultEmail, Sms = def.DefaultSms });
        }

        // Drop anything stored under a key that no longer exists, so a renamed event does not
        // linger forever as an unlabelled toggle.
        prefs.Events = prefs.Events.Where(e => NotificationEventKeys.IsKnown(e.EventKey)).ToList();

        return prefs;
    }

    public async Task SaveAsync(Guid userId, UserNotificationPreferencesDto preferences, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null) return;

        // Only known keys are persisted — the endpoint is user-facing, and an unbounded key set
        // would let anyone grow the blob indefinitely.
        var clean = new UserNotificationPreferencesDto
        {
            EmailEnabled = preferences.EmailEnabled,
            SmsEnabled = preferences.SmsEnabled,
            // Job state, not a preference — carried through so a round-trip of the panel does not
            // reset the digest gate and re-send the same week's digest.
            LastStaffDigestSentAt = preferences.LastStaffDigestSentAt,
            Events = preferences.Events
                .Where(e => NotificationEventKeys.IsKnown(e.EventKey))
                .GroupBy(e => e.EventKey)
                .Select(g => g.Last())
                .ToList()
        };

        user.NotificationPreferences = JsonSerializer.Serialize(clean);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Notification preferences saved for user {UserId}", userId);
    }
}
