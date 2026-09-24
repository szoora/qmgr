using QMgr.Application.Branding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using QMgr.Application.Interfaces;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;
using QMgr.Infrastructure.Data.Purge;

namespace QMgr.Infrastructure.Services;

/// <summary>
/// Scores a sign-up against existing accounts and decides whether to allow, flag or block it.
/// <para>
/// The design point worth keeping in mind when changing the weights: the two error types do not
/// cost the same. A farmed trial wastes a little storage and can be cleaned up afterwards; a real
/// customer who is wrongly refused generally leaves without telling anyone. So only two signals
/// block outright, both near-certain, and everything softer merely puts the account in front of a
/// human while letting the customer through.
/// </para>
/// </summary>
public class RegistrationGuardService : IRegistrationGuardService
{
    private readonly QMgrDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly IPlatformSettingsService _platformSettings;
    private readonly ILogger<RegistrationGuardService> _logger;

    // ---- Weights. Each is "how much does this raise suspicion", out of the Flag threshold. ----
    private const int ScoreSameNormalizedName = 40;
    private const int ScoreSimilarName = 25;
    private const int ScoreSameUnverifiedPhone = 35;
    private const int ScoreSameContactName = 15;
    private const int ScoreSameAddressRecently = 20;
    private const int ScoreDisposableEmail = 30;
    /// <summary>
    /// Told the organization already uses Q-Mgr and continued. On its own it does not reach the flag
    /// threshold — a second campus is a real thing — but with anything else it does, which is the
    /// right shape: a reviewer sees it beside whatever else was odd.
    /// </summary>
    private const int ScoreContinuedPastExistingOrganization = 30;

    /// <summary>
    /// A sign-up that matches a tenant which was purged. Below the flag threshold on its own,
    /// deliberately: a school whose trial lapsed and has come back to buy properly is the common
    /// case, and refusing them would be the worst possible outcome. It flags when something else
    /// agrees with it.
    /// </summary>
    private const int ScoreMatchesPurgedTenant = 25;

    private const int ScoreHoneypotTripped = 100;
    private const int ScoreImplausiblyFast = 40;

    /// <summary>At or above this, the account is created but queued for review.</summary>
    private const int FlagThreshold = 50;

    /// <summary>Names at or above this similarity, inside the same bucket, count as a near match.</summary>
    private const double SimilarNameThreshold = 0.85;

    /// <summary>A human cannot read the form, choose a module and type their details this fast.</summary>
    private static readonly TimeSpan MinimumPlausibleFormTime = TimeSpan.FromSeconds(4);

    /// <summary>How far back a shared network address still counts for anything.</summary>
    private static readonly TimeSpan AddressCorrelationWindow = TimeSpan.FromHours(24);

    // ---- Velocity budget per network address. ----
    private const int MaxAttemptsPerHour = 3;
    private const int MaxAttemptsPerDay = 10;
    private const string AttemptCachePrefix = "reg-attempts:";

    public RegistrationGuardService(
        QMgrDbContext dbContext,
        IDistributedCache cache,
        IPlatformSettingsService platformSettings,
        ILogger<RegistrationGuardService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _platformSettings = platformSettings;
        _logger = logger;
    }

    public async Task<RegistrationRiskAssessment> AssessAsync(
        RegistrationRiskInput input,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<string>();
        var score = 0;
        Guid? matchedOrganizationId = null;

        var normalizedEmail = RegistrationIdentity.NormalizeEmail(input.Email) ?? string.Empty;
        var normalizedPhone = RegistrationIdentity.NormalizePhone(input.Phone);
        var normalizedName = RegistrationIdentity.NormalizeOrganizationName(input.OrganizationName);
        var blockingKey = RegistrationIdentity.BuildNameBlockingKey(input.OrganizationName);

        // ---- Bot tells. Cheap, and no honest applicant ever trips them. ----
        if (!string.IsNullOrWhiteSpace(input.HoneypotValue))
        {
            score += ScoreHoneypotTripped;
            signals.Add("A hidden field that is invisible to people was filled in, which means the form was submitted by a script.");
        }

        if (input.FormRenderedAt is { } renderedAt)
        {
            var elapsed = DateTime.UtcNow - renderedAt;
            if (elapsed > TimeSpan.Zero && elapsed < MinimumPlausibleFormTime)
            {
                score += ScoreImplausiblyFast;
                signals.Add($"The form was submitted {elapsed.TotalSeconds:F1} seconds after it loaded, which is faster than a person can complete it.");
            }
        }

        // ---- Block condition one: the same mailbox, however it was spelled. ----
        var emailOwner = await _dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new { u.OrganizationId })
            .FirstOrDefaultAsync(cancellationToken);

        if (emailOwner != null)
        {
            return new RegistrationRiskAssessment
            {
                Decision = RegistrationRiskDecision.Block,
                Score = 100,
                MatchedOrganizationId = emailOwner.OrganizationId,
                Signals = new[] { "That email address already has an account. Provider aliasing was folded, so a dotted or plus-tagged variant of an existing address is treated as the same mailbox." },
                ApplicantMessage = "An account already exists for that email address. Try signing in, or reset your password if you have forgotten it."
            };
        }

        // ---- Block condition two: a phone already proven to belong to a live account. ----
        if (!string.IsNullOrEmpty(normalizedPhone))
        {
            var verifiedOwner = await _dbContext.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.NormalizedPhone == normalizedPhone && u.PhoneVerifiedAt != null)
                .Select(u => new { u.OrganizationId })
                .FirstOrDefaultAsync(cancellationToken);

            if (verifiedOwner != null)
            {
                return new RegistrationRiskAssessment
                {
                    Decision = RegistrationRiskDecision.Block,
                    Score = 100,
                    MatchedOrganizationId = verifiedOwner.OrganizationId,
                    Signals = new[] { "This phone number has already been verified against another account." },
                    ApplicantMessage = "That phone number is already registered to an account. Sign in with it, or use a different number."
                };
            }

            // Same number, never verified: suspicious, but a family or office landline can be shared,
            // and a typo can collide. Score it, do not refuse it.
            var unverifiedMatch = await _dbContext.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(u => u.NormalizedPhone == normalizedPhone, cancellationToken);

            if (unverifiedMatch)
            {
                score += ScoreSameUnverifiedPhone;
                signals.Add("The same phone number is already on another account, though it was never verified there.");
            }
        }

        // ---- Disposable mailbox providers. ----
        var extraDomains = await GetAdministratorDisposableDomainsAsync();
        if (DisposableEmailDomains.IsDisposable(input.Email, extraDomains))
        {
            score += ScoreDisposableEmail;
            signals.Add($"The address uses {RegistrationIdentity.EmailDomain(input.Email)}, a known throwaway mailbox provider.");
        }

        // ---- A tenant that was PURGED, coming back. ----
        // The whole point of TenantTombstone. Without it a purged tenant re-registers the next day
        // with no signal at all — and the premise of the purge feature is a high volume of trials
        // that never convert, which is exactly the population that would come back.
        //
        // It matches on HASHES, never on stored personal data: the tombstone keeps a one-way hash
        // of the sign-up email's DOMAIN and of the organization's blocking key, and nothing else.
        // A match raises the score; it never refuses. A school whose trial lapsed and who has come
        // back to buy properly is the good case here, not the bad one.
        var emailDomainHash = TenantTombstoneHash.Of(RegistrationIdentity.EmailDomain(input.Email));
        var nameKeyHash = TenantTombstoneHash.Of(blockingKey);
        if (emailDomainHash != null || nameKeyHash != null)
        {
            var tombstone = await _dbContext.TenantTombstones.AsNoTracking()
                .Where(t => (emailDomainHash != null && t.EmailDomainHash == emailDomainHash)
                            || (nameKeyHash != null && t.NameKeyHash == nameKeyHash))
                .OrderByDescending(t => t.PurgedAt)
                .FirstOrDefaultAsync();

            if (tombstone != null)
            {
                score += ScoreMatchesPurgedTenant;
                signals.Add($"An account matching this one was deleted on {tombstone.PurgedAt:dd MMM yyyy}. That may simply be the same school coming back, and the data itself is gone.");
            }
        }

        // ---- Business name. Narrowed by the blocking key, then compared in process. ----
        if (!string.IsNullOrEmpty(normalizedName))
        {
            var candidates = await _dbContext.Organizations
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(o => o.NameBlockingKey == blockingKey && o.Status != TenantStatus.Deleted)
                .Select(o => new { o.Id, o.NormalizedName })
                .Take(200)
                .ToListAsync(cancellationToken);

            var exact = candidates.FirstOrDefault(c => c.NormalizedName == normalizedName);
            if (exact != null)
            {
                score += ScoreSameNormalizedName;
                matchedOrganizationId = exact.Id;
                signals.Add("An organization with the same name already exists, once legal suffixes and word order are ignored.");
            }
            else
            {
                var best = candidates
                    .Select(c => new { c.Id, Similarity = RegistrationIdentity.NameSimilarity(normalizedName, c.NormalizedName) })
                    .OrderByDescending(x => x.Similarity)
                    .FirstOrDefault();

                if (best != null && best.Similarity >= SimilarNameThreshold)
                {
                    score += ScoreSimilarName;
                    matchedOrganizationId = best.Id;
                    signals.Add($"An organization with a very similar name already exists ({best.Similarity:P0} match).");
                }
            }
        }

        // ---- The same person's name behind a different business. ----
        if (!string.IsNullOrWhiteSpace(input.FirstName) && !string.IsNullOrWhiteSpace(input.LastName))
        {
            var first = input.FirstName.Trim().ToLowerInvariant();
            var last = input.LastName.Trim().ToLowerInvariant();

            var sameContact = await _dbContext.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(u => u.FirstName.ToLower() == first && u.LastName.ToLower() == last, cancellationToken);

            if (sameContact)
            {
                score += ScoreSameContactName;
                signals.Add("Another account is already registered to someone with the same first and last name.");
            }
        }

        // ---- Shared network address. The weakest signal here, and deliberately so. ----
        var addressHash = RegistrationIdentity.Fingerprint(input.ClientAddress);
        if (!string.IsNullOrEmpty(addressHash))
        {
            var since = DateTime.UtcNow - AddressCorrelationWindow;
            var recentFromSameAddress = await _dbContext.RegistrationAttempts
                .AsNoTracking()
                .CountAsync(a => a.ClientAddressHash == addressHash && a.CreatedAt >= since, cancellationToken);

            if (recentFromSameAddress > 0)
            {
                score += ScoreSameAddressRecently;
                signals.Add($"{recentFromSameAddress} other sign-up(s) came from the same network in the last day. Mobile networks and offices share addresses, so on its own this means little.");
            }
        }

        if (input.AcknowledgedExistingOrganization)
        {
            score += ScoreContinuedPastExistingOrganization;
            signals.Add(string.IsNullOrWhiteSpace(input.AcknowledgedOrganizationName)
                ? "They were told an organization at their email domain already uses " + ProductBrand.Name + ", and registered a new one anyway."
                : $"They were told \"{input.AcknowledgedOrganizationName}\" already uses {ProductBrand.Name} at their email domain, and registered a new one anyway.");
        }

        var decision = score >= FlagThreshold ? RegistrationRiskDecision.Flag : RegistrationRiskDecision.Allow;

        if (decision == RegistrationRiskDecision.Flag)
        {
            _logger.LogInformation(
                "Registration for {Email} flagged for review with score {Score}: {Signals}",
                normalizedEmail, score, string.Join(" | ", signals));
        }

        return new RegistrationRiskAssessment
        {
            Decision = decision,
            Score = score,
            Signals = signals,
            MatchedOrganizationId = matchedOrganizationId
        };
    }

    public async Task RecordAsync(
        RegistrationRiskInput input,
        RegistrationRiskAssessment assessment,
        Guid? organizationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _dbContext.RegistrationAttempts.Add(new RegistrationAttempt
            {
                Email = Truncate(input.Email, 256),
                NormalizedEmail = Truncate(RegistrationIdentity.NormalizeEmail(input.Email) ?? string.Empty, 256),
                Phone = Truncate(input.Phone, 32),
                NormalizedPhone = Truncate(RegistrationIdentity.NormalizePhone(input.Phone), 32),
                PhoneWasVerified = input.PhoneVerified,
                OrganizationName = Truncate(input.OrganizationName, 200),
                NormalizedName = Truncate(RegistrationIdentity.NormalizeOrganizationName(input.OrganizationName), 200),
                NameBlockingKey = Truncate(RegistrationIdentity.BuildNameBlockingKey(input.OrganizationName), 32),
                ContactFirstName = Truncate(input.FirstName, 100),
                ContactLastName = Truncate(input.LastName, 100),
                ClientAddressHash = RegistrationIdentity.Fingerprint(input.ClientAddress),
                Decision = assessment.Decision,
                RiskScore = assessment.Score,
                Signals = assessment.Signals.Count == 0 ? null : string.Join('\n', assessment.Signals),
                OrganizationId = organizationId,
                MatchedOrganizationId = assessment.MatchedOrganizationId
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The account already exists at this point. Losing the audit row is regrettable; failing
            // the customer's registration because we could not write it would be worse.
            _logger.LogError(ex, "Could not record the registration attempt for {Email}", input.Email);
        }
    }

    public async Task ResetAttemptBudgetAsync(string? clientAddress, CancellationToken cancellationToken = default)
    {
        var hash = RegistrationIdentity.Fingerprint(clientAddress);
        if (string.IsNullOrEmpty(hash)) return;

        var now = DateTime.UtcNow;
        await _cache.RemoveAsync($"{AttemptCachePrefix}h:{hash}:{now:yyyyMMddHH}", cancellationToken);
        await _cache.RemoveAsync($"{AttemptCachePrefix}d:{hash}:{now:yyyyMMdd}", cancellationToken);
    }

    public async Task<(bool Allowed, int RetryAfterSeconds)> TryConsumeAttemptAsync(
        string? clientAddress,
        CancellationToken cancellationToken = default)
    {
        var hash = RegistrationIdentity.Fingerprint(clientAddress);
        if (string.IsNullOrEmpty(hash)) return (true, 0);

        var now = DateTime.UtcNow;
        var hourKey = $"{AttemptCachePrefix}h:{hash}:{now:yyyyMMddHH}";
        var dayKey = $"{AttemptCachePrefix}d:{hash}:{now:yyyyMMdd}";

        var hourly = await IncrementAsync(hourKey, TimeSpan.FromHours(1), cancellationToken);
        if (hourly > MaxAttemptsPerHour)
        {
            var secondsToNextHour = (int)(3600 - (now.Minute * 60 + now.Second));
            return (false, Math.Max(secondsToNextHour, 1));
        }

        var daily = await IncrementAsync(dayKey, TimeSpan.FromDays(1), cancellationToken);
        if (daily > MaxAttemptsPerDay)
        {
            var secondsToMidnight = (int)(now.Date.AddDays(1) - now).TotalSeconds;
            return (false, Math.Max(secondsToMidnight, 1));
        }

        return (true, 0);
    }

    /// <summary>
    /// Reads any extra throwaway domains a platform administrator has configured, so the built-in
    /// list can be extended without a deploy. Failure is not fatal: the built-in list still applies.
    /// </summary>
    private async Task<IEnumerable<string>?> GetAdministratorDisposableDomainsAsync()
    {
        try
        {
            var saas = await _platformSettings.GetSettingsAsync<SaasSettings>("SaaS");
            return saas?.BlockedEmailDomains;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the configured blocked email domains; using the built-in list only");
            return null;
        }
    }

    private async Task<int> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var current = await _cache.GetStringAsync(key, cancellationToken);
        var next = (int.TryParse(current, out var parsed) ? parsed : 0) + 1;

        await _cache.SetStringAsync(
            key,
            next.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = window },
            cancellationToken);

        return next;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
