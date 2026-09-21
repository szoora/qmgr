using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Infrastructure.Email;

namespace QMgr.Infrastructure.Data.Purge;

/// <inheritdoc cref="ITenantLifecycleService"/>
public sealed class TenantLifecycleService : ITenantLifecycleService
{
    /// <summary>
    /// How long a tenant sits in each state before the daily sweep moves it on.
    ///
    /// THESE ARE PUBLISHED, not internal. Microsoft's 90 days works as a promise a customer can
    /// plan around; a grace period nobody is told about is just latency. The Terms say these
    /// numbers, the warning emails repeat them, and the platform UI counts them down.
    /// </summary>
    public static TimeSpan? DwellFor(TenantStatus status) => status switch
    {
        TenantStatus.Pending => TimeSpan.FromDays(14),          // signed up, never verified
        TenantStatus.Suspended => TimeSpan.FromDays(60),
        TenantStatus.Cancelled => TimeSpan.FromDays(30),
        TenantStatus.PendingDeletion => TimeSpan.FromDays(14),  // the last, reversible window
        _ => null                                                // Trialing/Active are moved by billing, not by a clock here
    };

    /// <summary>What a state becomes when its clock runs out.</summary>
    public static TenantStatus? NextAfter(TenantStatus status) => status switch
    {
        TenantStatus.Pending => TenantStatus.Suspended,
        TenantStatus.Suspended => TenantStatus.Cancelled,
        TenantStatus.Cancelled => TenantStatus.PendingDeletion,
        TenantStatus.PendingDeletion => TenantStatus.Deleted,   // "Deleted" here means PURGED: no row survives
        _ => null
    };

    /// <summary>How long before a transition the tenant is warned. Null means no warning is due for that state.</summary>
    private static TimeSpan? WarnBefore(TenantStatus status) => status switch
    {
        TenantStatus.Suspended => TimeSpan.FromDays(14),        // "your data will be closed in a fortnight"
        TenantStatus.Cancelled => TimeSpan.FromDays(7),
        TenantStatus.PendingDeletion => TimeSpan.FromDays(7),   // the last one that matters
        _ => null
    };

    private readonly QMgrDbContext _db;
    private readonly IEmailSender _email;
    private readonly IEmailBrandService _brands;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly ITenantPurgeService _purge;
    private readonly ILogger<TenantLifecycleService> _logger;

    public TenantLifecycleService(
        QMgrDbContext db,
        IEmailSender email,
        IEmailBrandService brands,
        IPlatformSettingsService platformSettings,
        ITenantPurgeService purge,
        ILogger<TenantLifecycleService> logger)
    {
        _db = db;
        _email = email;
        _brands = brands;
        _platformSettings = platformSettings;
        _purge = purge;
        _logger = logger;
    }

    public async Task<TenantLifecycleResult> TransitionAsync(Guid organizationId, TenantStatus to, string actor, Guid? actorUserId, string? reason, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null)
            return new TenantLifecycleResult(false, "Organisation not found.", new TenantLifecycleStatusDto());

        var from = org.Status;
        if (from == to)
            return new TenantLifecycleResult(true, null, await GetStatusAsync(organizationId, cancellationToken));

        // Deleted is what a PURGE leaves, and a purge is the only thing that may set it — otherwise
        // a tenant ends up in a state whose whole meaning is "there is no row here" while its row
        // is very much here, which is the confusion this lifecycle exists to remove.
        if (to == TenantStatus.Deleted)
            return new TenantLifecycleResult(false, "A tenant reaches Deleted only by being purged.", await GetStatusAsync(organizationId, cancellationToken));

        org.Status = to;
        org.UpdatedAt = DateTime.UtcNow;

        var dwell = DwellFor(to);
        var due = dwell.HasValue ? DateTime.UtcNow.Add(dwell.Value) : (DateTime?)null;

        _db.Set<TenantLifecycleEvent>().Add(new TenantLifecycleEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            OrganizationName = org.Name,
            FromStatus = from,
            ToStatus = to,
            OccurredAt = DateTime.UtcNow,
            ActorUserId = actorUserId,
            Actor = actor,
            Reason = reason,
            NextTransitionDueAt = due,
        });

        await _db.SaveChangesAsync(cancellationToken);

        await TellTenantAsync(org, to, due, cancellationToken);

        _logger.LogInformation("Tenant {OrganizationId} moved {From} -> {To} by {Actor}", organizationId, from, to, actor);
        return new TenantLifecycleResult(true, null, await GetStatusAsync(organizationId, cancellationToken));
    }

    public async Task<TenantLifecycleStatusDto> GetStatusAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var org = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null) return new TenantLifecycleStatusDto { OrganizationId = organizationId };

        var history = await _db.Set<TenantLifecycleEvent>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(20)
            .Select(e => new TenantLifecycleEventDto
            {
                FromStatus = e.FromStatus == null ? null : e.FromStatus.ToString(),
                ToStatus = e.ToStatus.ToString(),
                OccurredAt = e.OccurredAt,
                Actor = e.Actor,
                Reason = e.Reason,
            })
            .ToListAsync(cancellationToken);

        var due = await DueAtAsync(org.Id, org.Status, org.UpdatedAt ?? org.CreatedAt, cancellationToken);
        var next = NextAfter(org.Status);

        var hasFinancials = await _db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.OrganizationId == organizationId, cancellationToken)
            || await _db.Payments.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == organizationId, cancellationToken);

        return new TenantLifecycleStatusDto
        {
            OrganizationId = organizationId,
            Status = org.Status.ToString(),
            NextTransitionDueAt = due,
            NextStatus = next?.ToString(),
            DaysUntilNextTransition = due.HasValue ? Math.Max(0, (int)Math.Ceiling((due.Value - DateTime.UtcNow).TotalDays)) : null,
            IsScheduledForDeletion = org.Status == TenantStatus.PendingDeletion,
            HasFinancialRecords = hasFinancials,
            History = history,
        };
    }

    /// <summary>
    /// When the clock on the CURRENT state runs out. Read from the last lifecycle event where one
    /// exists — a tenant that has been through this pipeline has an authoritative date — and
    /// otherwise from the organisation's own UpdatedAt, so tenants that predate the feature still
    /// have a sensible clock rather than none.
    /// </summary>
    private async Task<DateTime?> DueAtAsync(Guid organizationId, TenantStatus status, DateTime updatedAt, CancellationToken cancellationToken)
    {
        var dwell = DwellFor(status);
        if (dwell == null) return null;

        var last = await _db.Set<TenantLifecycleEvent>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.ToStatus == status)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        return (last?.OccurredAt ?? updatedAt).Add(dwell.Value);
    }

    public async Task<int> RunDueTransitionsAsync(CancellationToken cancellationToken = default)
    {
        var moved = 0;

        // Only the states a clock moves. Trialing and Active are billing's business, and the
        // real-time trial expiry already lives in TenantStatusMiddleware.
        var clocked = new[] { TenantStatus.Pending, TenantStatus.Suspended, TenantStatus.Cancelled, TenantStatus.PendingDeletion };

        var candidates = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => clocked.Contains(o.Status))
            .Select(o => new { o.Id, o.Status, o.UpdatedAt, o.CreatedAt, o.Name })
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var due = await DueAtAsync(candidate.Id, candidate.Status, candidate.UpdatedAt ?? candidate.CreatedAt, cancellationToken);
            if (due == null) continue;

            if (DateTime.UtcNow < due.Value)
            {
                await MaybeWarnAsync(candidate.Id, candidate.Status, due.Value, cancellationToken);
                continue;
            }

            var next = NextAfter(candidate.Status);
            if (next == null) continue;

            if (next == TenantStatus.Deleted)
            {
                // The clock has run out on the last window. This is the ONLY automatic path to a
                // purge, and it is reached only from PendingDeletion — which a human put the tenant
                // into, or a clock did 14 days after Cancelled, which was itself 30 days after
                // Suspended. Nothing a customer can press reaches here.
                var result = await _purge.PurgeAsync(candidate.Id, "tenant-lifecycle-sweep", null, "retention period elapsed", cancellationToken);
                if (result.Ok) moved++;
                else _logger.LogError("Scheduled purge of {OrganizationId} failed verification and was rolled back: {Detail}", candidate.Id, result.VerificationDetail);
                continue;
            }

            var transition = await TransitionAsync(candidate.Id, next.Value, "tenant-lifecycle-sweep", null, "retention period elapsed", cancellationToken);
            if (transition.Ok) moved++;
        }

        if (candidates.Count > 0)
            _logger.LogInformation("Tenant lifecycle sweep: {Checked} checked, {Moved} advanced", candidates.Count, moved);

        return moved;
    }

    // ==========================================================================================

    /// <summary>
    /// The warning that falls due before a transition, sent once. "Once" is enforced by looking for
    /// a lifecycle event of the same shape — there is no separate flag to get out of step with the
    /// clock, and a tenant re-entering a state gets a fresh warning because its event is newer.
    /// </summary>
    private async Task MaybeWarnAsync(Guid organizationId, TenantStatus status, DateTime due, CancellationToken cancellationToken)
    {
        var warnBefore = WarnBefore(status);
        if (warnBefore == null) return;
        if (DateTime.UtcNow < due - warnBefore.Value) return;

        var marker = $"warned:{status}";
        var alreadyWarned = await _db.Set<TenantLifecycleEvent>().AsNoTracking()
            .AnyAsync(e => e.OrganizationId == organizationId && e.Reason == marker && e.ToStatus == status, cancellationToken);
        if (alreadyWarned) return;

        var org = await _db.Organizations.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(o => o.Id == organizationId, cancellationToken);
        if (org == null) return;

        await TellTenantAsync(org, status, due, cancellationToken, isWarning: true);

        _db.Set<TenantLifecycleEvent>().Add(new TenantLifecycleEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            OrganizationName = org.Name,
            FromStatus = status,
            ToStatus = status,
            OccurredAt = DateTime.UtcNow,
            Actor = "tenant-lifecycle-sweep",
            Reason = marker,
            NextTransitionDueAt = due,
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Tells the tenant what has happened or is about to. NEVER throws: a tenant whose mailbox
    /// bounces must not stop the lifecycle, and the clock is the record, not the email.
    /// </summary>
    private async Task TellTenantAsync(Domain.Entities.Organization.Organization org, TenantStatus status, DateTime? due, CancellationToken cancellationToken, bool isWarning = false)
    {
        var to = org.BillingEmail ?? org.ContactEmail;
        if (string.IsNullOrWhiteSpace(to)) return;

        var (title, body) = Message(org.Name, status, due, isWarning);
        if (title == null) return;

        try
        {
            var brand = await _brands.ForOrganizationAsync(org.Id, cancellationToken);
            var baseUrl = await _platformSettings.GetPublicWebBaseUrlAsync();
            var html = EmailTemplates.Layout(
                title, null, body,
                ctaText: status == TenantStatus.PendingDeletion || status == TenantStatus.Cancelled ? "Sign in and reactivate" : null,
                ctaUrl: EmailTemplates.Link(baseUrl, "/login"),
                tone: status is TenantStatus.PendingDeletion ? EmailTemplates.Tone.Warning : EmailTemplates.Tone.Info,
                brand: brand);

            await _email.SendAsync(to, title, html, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell tenant {OrganizationId} about its move to {Status}", org.Id, status);
        }
    }

    private static (string? Title, string[] Body) Message(string name, TenantStatus status, DateTime? due, bool isWarning)
    {
        var when = due?.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "shortly";

        return status switch
        {
            TenantStatus.Suspended when isWarning => ("Your account will be closed soon", [
                $"{EmailTemplates.B(name)} has been suspended and no one can sign in.",
                $"If nothing changes, the account will be CLOSED on {EmailTemplates.P(when)}. You can still reactivate it before then by signing in and paying for a module."]),
            TenantStatus.Suspended => ("Your account has been suspended", [
                $"{EmailTemplates.B(name)} has been suspended, so nobody can sign in at the moment.",
                $"Your data is untouched. Sign in and pay for a module to bring it back. If nothing changes, the account will be closed on {EmailTemplates.P(when)}."]),

            TenantStatus.Cancelled when isWarning => ("Your data will be scheduled for deletion", [
                $"{EmailTemplates.B(name)} is closed, and its data is still here.",
                $"On {EmailTemplates.P(when)} it will be scheduled for deletion. Export anything you need before then, or reactivate the account."]),
            TenantStatus.Cancelled => ("Your account is closed", [
                $"{EmailTemplates.B(name)} is now closed. Nobody can sign in, and no further charges will be made.",
                $"Your data is still here and you can still get it back by reactivating. On {EmailTemplates.P(when)} it will be scheduled for deletion."]),

            TenantStatus.PendingDeletion when isWarning => ("Final notice: your data is about to be deleted", [
                $"{EmailTemplates.B(name)} is scheduled for deletion on {EmailTemplates.P(when)}.",
                "After that date the deletion is permanent and cannot be undone — every record, every uploaded file, every account.",
                "If you want to keep it, sign in and reactivate before that date."]),
            TenantStatus.PendingDeletion => ("Your data is scheduled for deletion", [
                $"{EmailTemplates.B(name)} has been scheduled for deletion on {EmailTemplates.P(when)}.",
                "Until then it can still be restored. After it, the deletion is permanent and cannot be undone.",
                "If this is a mistake, sign in and reactivate, or reply to this message."]),

            _ => (null, [])
        };
    }
}
