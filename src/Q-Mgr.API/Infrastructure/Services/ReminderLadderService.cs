using QMgr.Application.DTOs;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Enums;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// The ONE escalating-reminder engine (duty rota plan §8.1). Every ladder — a Session duty coming up, a register
/// not taken, a rota slot, a report due, a lesson about to start, a timetable clash — is a list of stages
/// <c>{ offset, channels, audience, interruptive }</c> in the tenant policy, and a row stores only the highest
/// stage it has sent. This service answers the one question a sweep needs: which stage, if any, should go out
/// NOW. The claim (a conditional <c>UPDATE … WHERE stage &lt; @stage</c>) and the send stay with the sweep, which
/// knows the row and its audience.
///
/// Rules, all here and nowhere else:
/// <list type="bullet">
///   <item><b>Collapse.</b> Only the highest stage already due is sent. A slot created two days ahead sends stage 3
///   once, never stages 1–3 in a burst.</item>
///   <item><b>Quiet hours.</b> A non-interruptive stage that falls due inside quiet hours (branch-local, default
///   20:00–06:00) is held — the next sweep after quiet hours sends it, collapsed with anything that became due
///   meanwhile. An interruptive stage is sent anyway.</item>
///   <item><b>Pinned hours.</b> A stage with <c>AtLocalHour</c> is due at that branch-local hour on the day its
///   offset lands, not at the exact offset.</item>
/// </list>
/// </summary>
public interface IReminderLadderService
{
    /// <summary>When a stage becomes due, in UTC, for a row whose anchor (a start, a due time) is <paramref name="anchorUtc"/>.</summary>
    DateTime DueAtUtc(ReminderStageDto stage, DateTime anchorUtc, TimeZoneInfo zone);

    /// <summary>
    /// The stage to send now, collapsed, or null when nothing above <paramref name="sentStage"/> is due or the
    /// due stage is held by quiet hours.
    /// </summary>
    ReminderStageDto? DueStage(ReminderLadderDto ladder, DateTime anchorUtc, int sentStage, DateTime nowUtc, TimeZoneInfo zone, QuietHoursDto? quiet);

    /// <summary>True when <paramref name="nowUtc"/> falls inside the branch-local quiet hours.</summary>
    bool InQuietHours(DateTime nowUtc, TimeZoneInfo zone, QuietHoursDto? quiet);

    /// <summary>The notification channels a stage maps to. A Digest-only stage maps to none: the digest carries it.</summary>
    NotificationChannel ChannelsFor(ReminderStageDto stage);
}

public class ReminderLadderService : IReminderLadderService
{
    public DateTime DueAtUtc(ReminderStageDto stage, DateTime anchorUtc, TimeZoneInfo zone)
    {
        var raw = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc).AddMinutes(stage.OffsetMinutes);
        if (stage.AtLocalHour is not { } hour) return raw;

        var local = TimeZoneInfo.ConvertTimeFromUtc(raw, zone);
        var pinned = new DateTime(local.Year, local.Month, local.Day, Math.Clamp(hour, 0, 23), 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(pinned, zone);
    }

    public ReminderStageDto? DueStage(ReminderLadderDto ladder, DateTime anchorUtc, int sentStage, DateTime nowUtc, TimeZoneInfo zone, QuietHoursDto? quiet)
    {
        var due = ladder.Stages
            .Where(s => s.Stage > sentStage && DueAtUtc(s, anchorUtc, zone) <= nowUtc)
            .OrderByDescending(s => s.Stage)
            .FirstOrDefault();
        if (due == null) return null;
        if (!due.Interruptive && InQuietHours(nowUtc, zone, quiet)) return null;
        return due;
    }

    public bool InQuietHours(DateTime nowUtc, TimeZoneInfo zone, QuietHoursDto? quiet)
    {
        if (quiet is not { Enabled: true } || quiet.StartHour == quiet.EndHour) return false;
        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Hour;
        return quiet.StartHour > quiet.EndHour
            ? hour >= quiet.StartHour || hour < quiet.EndHour   // 20:00–06:00, across midnight
            : hour >= quiet.StartHour && hour < quiet.EndHour;  // 13:00–14:00, same day
    }

    public NotificationChannel ChannelsFor(ReminderStageDto stage)
    {
        NotificationChannel channels = 0;
        if (stage.Channels.HasFlag(ReminderChannels.Bell)) channels |= NotificationChannel.InApp;
        if (stage.Channels.HasFlag(ReminderChannels.Email)) channels |= NotificationChannel.Email;
        // SMS is preference-aware downstream: the event key's DefaultSms is off, so it reaches only a person who
        // switched it on, and the dispatcher skips it for a tenant with no SMS provider.
        if (stage.Channels.HasFlag(ReminderChannels.Sms)) channels |= NotificationChannel.Sms;
        return channels;
    }
}
