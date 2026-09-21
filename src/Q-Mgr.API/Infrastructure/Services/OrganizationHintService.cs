using Microsoft.EntityFrameworkCore;
using QMgr.Application.DTOs;
using QMgr.Application.Interfaces;
using QMgr.Domain.Enums;
using QMgr.Domain.Identity;
using QMgr.Infrastructure.Data;

namespace QMgr.Infrastructure.Services;

/// <inheritdoc />
public class OrganizationHintService : IOrganizationHintService
{
    private readonly QMgrDbContext _db;
    private readonly IStaffOnboardingPolicyService _onboarding;
    private readonly ILogger<OrganizationHintService> _logger;

    public OrganizationHintService(
        QMgrDbContext db,
        IStaffOnboardingPolicyService onboarding,
        ILogger<OrganizationHintService> logger)
    {
        _db = db;
        _onboarding = onboarding;
        _logger = logger;
    }

    private static readonly OrganizationHintDto None = new() { Found = false };

    public async Task<OrganizationHintDto> LookUpAsync(string? email, CancellationToken cancellationToken = default)
    {
        // The domain comes from RegistrationIdentity, the one home for turning an address into a
        // canonical form — a second lower-case-and-split helper here is this codebase's most
        // repeated bug.
        var domain = RegistrationIdentity.EmailDomain(email);
        if (string.IsNullOrWhiteSpace(domain)) return None;

        // A free mailbox says nothing about an organization: every school in the country has
        // teachers on gmail.com, and naming a tenant on that basis would tell a stranger which
        // school uses the product. Only a domain a tenant published about ITSELF can match.
        if (PublicMailboxes.Contains(domain)) return None;

        // Only tenants that switched staff self-sign-up ON are candidates — that is the consent.
        // The set is small by construction (most tenants never turn it on), so the domain match
        // itself is done in memory rather than as a jsonb query; a raw jsonb containment query here
        // would also have to be schema-qualified by hand for no gain (CLAUDE.md).
        List<(Guid Id, string Name, string? Settings)> candidates;
        try
        {
            candidates = await _db.Organizations
                .FromSqlInterpolated($"SELECT * FROM qmgr.organizations WHERE \"Settings\"->'StaffOnboarding'->>'JoinEnabled' = 'true'")
                .IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Status == TenantStatus.Active || o.Status == TenantStatus.Trialing)
                .Select(o => new ValueTuple<Guid, string, string?>(o.Id, o.Name, o.Settings))
                .Take(500)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A hint that cannot be produced must never fail a sign-up: this only ever ADDS a
            // warning, so its absence costs a warning and nothing else.
            _logger.LogError(ex, "Organization hint lookup failed for domain {Domain}", domain);
            return None;
        }

        foreach (var (id, name, settings) in candidates)
        {
            var policy = _onboarding.ReadPolicy(settings);
            if (!policy.JoinEnabled) continue;
            if (policy.JoinCodeHash == null) continue;                       // nothing to join with
            if (policy.JoinCodeExpiresAt is { } ends && ends <= DateTime.UtcNow) continue;

            var listed = policy.AllowedEmailDomains
                .Select(d => d?.Trim().TrimStart('@').ToLowerInvariant())
                .Any(d => !string.IsNullOrEmpty(d) && d == domain);
            if (!listed) continue;

            return new OrganizationHintDto { Found = true, OrganizationName = name, MatchedDomain = domain };
        }

        return None;
    }

    /// <summary>
    /// Mailbox providers anybody can sign up to. A match on one of these is a match on nothing, and
    /// answering for them would turn this into a way of enumerating which schools use the product.
    /// </summary>
    private static readonly HashSet<string> PublicMailboxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.uk", "ymail.com",
        "outlook.com", "hotmail.com", "hotmail.co.uk", "live.com", "msn.com",
        "icloud.com", "me.com", "aol.com", "proton.me", "protonmail.com",
        "zoho.com", "gmx.com", "mail.com", "yandex.com", "rocketmail.com",
    };
}
