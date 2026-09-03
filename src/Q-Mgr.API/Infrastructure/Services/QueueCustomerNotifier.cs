using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Email;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Tells the customer holding a ticket what the queue is doing — see
/// <see cref="IQueueCustomerNotifier"/> for why this exists and for the no-throw contract.
///
/// SHAPE: registered as a singleton and dispatches every send onto a background task with its
/// own DI scope (<see cref="IServiceScopeFactory"/>). Two reasons, both about the caller:
///  1. Nothing it does can fail a queue operation — the caller never observes an exception.
///  2. Nothing it does can slow one down. SMTP in particular is a synchronous, multi-second
///     network round trip, and "nearly your turn" can fan out to several recipients at once;
///     doing that inline would put all of it on the critical path of every Call Next.
/// The trade-off is that a message in flight is lost if the process stops — acceptable for a
/// courtesy notification, and the alternative (a durable outbox) is a much larger change.
/// </summary>
public class QueueCustomerNotifier : IQueueCustomerNotifier
{
    // Token.LastNotifiedStage values. Text rather than an enum so no new Domain enum type is
    // needed and the column reads plainly in the database.
    public const string StageIssued = "Issued";
    public const string StageApproaching = "Approaching";
    public const string StageCalled = "Called";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueueCustomerNotifier> _logger;

    public QueueCustomerNotifier(IServiceScopeFactory scopeFactory, ILogger<QueueCustomerNotifier> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task NotifyTicketIssuedAsync(Guid tokenId, int positionInQueue)
    {
        Dispatch("ticket issued", async (db, notifications, ct) =>
        {
            // Include the service type: there is no lazy loading configured on this context, and
            // {ServiceType} is a supported placeholder.
            var token = await db.Tokens.Include(t => t.ServiceType).FirstOrDefaultAsync(t => t.Id == tokenId, ct);
            if (token == null) return;

            var ctx = await LoadContextAsync(db, token.BranchId, ct);
            if (ctx == null || !ctx.Settings.QueueNotifyOnIssued) return;

            // GetQueuePositionAsync returns 0 for a token that is no longer waiting; treat that
            // as "no position" rather than printing "#0".
            var placeholders = BuildPlaceholders(token, ctx, positionInQueue > 0 ? (int?)positionInQueue : null, counter: null);

            var sent = await SendAsync(
                notifications, ctx, token, placeholders,
                smsTemplate: Fallback(ctx.Settings.SmsTokenCreatedTemplate, NotificationSettings.DefaultSmsIssued),
                emailSubjectTemplate: Fallback(ctx.Settings.EmailTokenCreatedSubject, NotificationSettings.DefaultEmailIssuedSubject),
                emailBodyTemplate: Fallback(ctx.Settings.EmailTokenCreatedTemplate, NotificationSettings.DefaultEmailIssuedBody),
                ct);

            if (sent)
            {
                await MarkStageAsync(db, token, StageIssued, ct);
            }
        });

        return Task.CompletedTask;
    }

    public Task NotifyCalledToCounterAsync(Guid tokenId, Guid counterId)
    {
        Dispatch("token called", async (db, notifications, ct) =>
        {
            // Include the service type: there is no lazy loading configured on this context, and
            // {ServiceType} is a supported placeholder.
            var token = await db.Tokens.Include(t => t.ServiceType).FirstOrDefaultAsync(t => t.Id == tokenId, ct);
            if (token == null) return;

            var ctx = await LoadContextAsync(db, token.BranchId, ct);
            if (ctx == null || !ctx.Settings.QueueNotifyOnCalled) return;

            var counter = await db.Counters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == counterId, ct);
            var placeholders = BuildPlaceholders(token, ctx, position: null, counter);

            var sent = await SendAsync(
                notifications, ctx, token, placeholders,
                smsTemplate: Fallback(ctx.Settings.SmsTokenCalledTemplate, NotificationSettings.DefaultSmsCalled),
                emailSubjectTemplate: Fallback(ctx.Settings.EmailTokenCalledSubject, NotificationSettings.DefaultEmailCalledSubject),
                emailBodyTemplate: Fallback(ctx.Settings.EmailTokenCalledTemplate, NotificationSettings.DefaultEmailCalledBody),
                ct);

            if (sent)
            {
                await MarkStageAsync(db, token, StageCalled, ct);
            }
        });

        return Task.CompletedTask;
    }

    public Task NotifyApproachingTurnAsync(Guid branchId, Guid serviceTypeId)
    {
        Dispatch("approaching turn", async (db, notifications, ct) =>
        {
            var ctx = await LoadContextAsync(db, branchId, ct);
            if (ctx == null || !ctx.Settings.QueueNotifyOnApproaching) return;

            var threshold = ctx.Settings.SmsLeadTokens;
            if (threshold <= 0) return;

            // One query for the whole front of this queue. Ordering matches
            // TokenRepository.GetWaitingTokensAsync / GetQueuePositionAsync exactly (priority
            // first, then arrival), so index + 1 IS the customer's position — no per-token
            // position query.
            var upcoming = await db.Tokens
                .Include(t => t.ServiceType)
                .Where(t => t.BranchId == branchId
                            && t.ServiceTypeId == serviceTypeId
                            && t.Status == TokenStatus.Waiting)
                .OrderBy(t => t.Priority)
                .ThenBy(t => t.CreatedAt)
                .Take(threshold)
                .ToListAsync(ct);

            var anySent = false;

            for (var i = 0; i < upcoming.Count; i++)
            {
                var token = upcoming[i];

                // Already warned (or already called) — never message the same person twice for
                // the same reason, however many times Call Next is pressed behind them.
                if (token.LastNotifiedStage is StageApproaching or StageCalled)
                    continue;

                var placeholders = BuildPlaceholders(token, ctx, i + 1, counter: null);

                var sent = await SendAsync(
                    notifications, ctx, token, placeholders,
                    smsTemplate: Fallback(ctx.Settings.SmsReminderTemplate, NotificationSettings.DefaultSmsApproaching),
                    emailSubjectTemplate: Fallback(ctx.Settings.EmailReminderSubject, NotificationSettings.DefaultEmailApproachingSubject),
                    emailBodyTemplate: Fallback(ctx.Settings.EmailReminderTemplate, NotificationSettings.DefaultEmailApproachingBody),
                    ct);

                if (sent)
                {
                    token.LastNotifiedStage = StageApproaching;
                    token.LastNotifiedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
                    anySent = true;
                }
            }

            if (anySent)
            {
                await db.SaveChangesAsync(ct);
            }
        });

        return Task.CompletedTask;
    }

    #region Sending

    /// <summary>
    /// Sends this moment's message on whichever channels are both switched on and actually
    /// usable for this customer. Returns true if at least one channel accepted it.
    ///
    /// CONSENT AND COST: a channel is used only when the customer supplied that contact detail
    /// on the ticket. A ticket created by staff with no phone number simply gets no SMS — there
    /// is no fallback to any other number. A branch whose SMS credentials are missing degrades
    /// to nothing: INotificationService.SendSmsAsync/SendEmailAsync return false rather than
    /// throwing when the channel is disabled or unconfigured.
    /// </summary>
    private async Task<bool> SendAsync(
        INotificationService notifications,
        QueueContext ctx,
        Token token,
        IReadOnlyDictionary<string, string> placeholders,
        string smsTemplate,
        string emailSubjectTemplate,
        string emailBodyTemplate,
        CancellationToken ct)
    {
        var settings = ctx.Settings;
        var sent = false;

        if (settings.QueueNotifySms && settings.SmsEnabled && !string.IsNullOrWhiteSpace(token.CustomerPhone))
        {
            try
            {
                var body = Render(smsTemplate, placeholders, htmlEncodeValues: false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    sent |= await notifications.SendSmsAsync(ctx.OrganizationId, token.CustomerPhone!.Trim(), body, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue SMS to token {TokenId} failed", token.Id);
            }
        }

        if (settings.QueueNotifyEmail && settings.EmailEnabled && !string.IsNullOrWhiteSpace(token.CustomerEmail))
        {
            try
            {
                var subject = Render(emailSubjectTemplate, placeholders, htmlEncodeValues: false);
                var rendered = Render(emailBodyTemplate, placeholders, htmlEncodeValues: true);
                var paragraphs = rendered
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();

                if (paragraphs.Count > 0)
                {
                    var html = EmailTemplates.Layout(
                        title: string.IsNullOrWhiteSpace(subject) ? "Queue update" : subject,
                        greeting: string.IsNullOrWhiteSpace(token.CustomerName) ? null : token.CustomerName,
                        paragraphs: paragraphs,
                        footerNote: $"You are receiving this because you took ticket {WebUtility.HtmlEncode(token.DisplayNumber)} at {WebUtility.HtmlEncode(ctx.BranchName)}.");

                    sent |= await notifications.SendEmailAsync(
                        ctx.OrganizationId, token.CustomerEmail!.Trim(), subject, html, true, null, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue email to token {TokenId} failed", token.Id);
            }
        }

        return sent;
    }

    private static async Task MarkStageAsync(QMgrDbContext db, Token token, string stage, CancellationToken ct)
    {
        token.LastNotifiedStage = stage;
        token.LastNotifiedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        await db.SaveChangesAsync(ct);
    }

    #endregion

    #region Context and templating

    /// <summary>
    /// Branch name, organization name/id and the organization's notification settings.
    /// IgnoreQueryFilters is deliberate: this runs on a background task with no HTTP request
    /// behind it, so the tenant query filter has no resolved context to work from. Every lookup
    /// here is by a primary key derived from the token itself and is used only to address that
    /// same customer, so there is no cross-tenant read to leak.
    /// </summary>
    private static async Task<QueueContext?> LoadContextAsync(QMgrDbContext db, Guid branchId, CancellationToken ct)
    {
        var branch = await db.Branches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == branchId, ct);
        if (branch == null) return null;

        var settings = await db.NotificationSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == branch.OrganizationId, ct);

        // No settings row at all, or the master switch is off — nothing to do. Note this is the
        // "not configured" path, not an error: it is what a brand-new organization looks like.
        if (settings == null || !settings.QueueNotificationsEnabled) return null;

        var organizationName = await db.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == branch.OrganizationId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct);

        return new QueueContext(branch.OrganizationId, branch.Name, organizationName ?? string.Empty, settings);
    }

    private static Dictionary<string, string> BuildPlaceholders(Token token, QueueContext ctx, int? position, Counter? counter)
    {
        var ticket = string.IsNullOrWhiteSpace(token.DisplayNumber) ? token.TokenNumber : token.DisplayNumber;
        var counterNumber = counter?.CounterNumber ?? string.Empty;
        var counterName = counter?.DisplayName ?? counter?.CounterNumber ?? string.Empty;
        var positionText = position?.ToString() ?? string.Empty;
        var waitText = token.EstimatedWaitMinutes?.ToString() ?? string.Empty;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TicketNumber"] = ticket,
            ["CounterNumber"] = counterNumber,
            ["Position"] = positionText,
            ["BranchName"] = ctx.BranchName,
            ["OrganizationName"] = ctx.OrganizationName,
            ["EstimatedWaitMinutes"] = waitText,
            ["ServiceType"] = token.ServiceType?.Name ?? string.Empty,
            ["CustomerName"] = token.CustomerName ?? string.Empty,

            // Legacy aliases — templates saved before queue notifications existed use these.
            ["TokenNumber"] = ticket,
            ["CounterName"] = counterName,
            ["PositionInQueue"] = positionText,
            ["WaitTime"] = waitText,
            ["TrackingUrl"] = string.Empty
        };
    }

    private static string Fallback(string? configured, string @default)
        => string.IsNullOrWhiteSpace(configured) ? @default : configured;

    /// <summary>
    /// Substitutes {Placeholder} tokens, case-insensitively. Values are HTML-encoded for email
    /// (the template itself is admin-authored HTML and stays raw; the customer/branch data
    /// substituted into it must not be able to break the markup) and left alone for SMS.
    /// Whitespace left behind by an empty value is collapsed so a message never reads
    /// "counter  at" or ends with a dangling separator.
    /// </summary>
    private static string Render(string template, IReadOnlyDictionary<string, string> values, bool htmlEncodeValues)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            var replacement = htmlEncodeValues ? WebUtility.HtmlEncode(value) : value;
            result = result.Replace("{" + key + "}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        var lines = result
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim());

        return string.Join('\n', lines).Trim();
    }

    private sealed record QueueContext(Guid OrganizationId, string BranchName, string OrganizationName, NotificationSettings Settings);

    #endregion

    #region Dispatch

    private void Dispatch(string what, Func<QMgrDbContext, INotificationService, CancellationToken, Task> work)
    {
        // CancellationToken.None on purpose: the caller's request token is cancelled the moment
        // the HTTP response completes, which is exactly when this work is still running.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QMgrDbContext>();
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
                await work(db, notifications, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue customer notification ({What}) failed", what);
            }
        });
    }

    #endregion
}
