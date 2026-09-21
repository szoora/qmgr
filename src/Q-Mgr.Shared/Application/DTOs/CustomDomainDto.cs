namespace QMgr.Application.DTOs;

/// <summary>
/// Where a tenant's own domain has got to. Lives in Q-Mgr.Shared because the platform Tenants
/// page renders exactly this and the API produces it — the DTO-duplication rule that has already
/// bitten this codebase six times (OrganizationBrandingDto, ContentDto, NotificationDto,
/// UserInfo, SubscriptionPlan, RoleListDto).
/// </summary>
public record CustomDomainStatusDto
{
    public CustomDomainState State { get; init; } = CustomDomainState.None;

    /// <summary>The live host, once verified and certificated. Null otherwise.</summary>
    public string? Domain { get; init; }

    /// <summary>The host being verified, while <see cref="State"/> is Pending or Verified.</summary>
    public string? PendingDomain { get; init; }

    /// <summary>The DNS record that PROVES the tenant owns the name. Null once nothing is left to prove.</summary>
    public string? VerificationRecordName { get; init; }
    public string? VerificationRecordValue { get; init; }

    /// <summary>
    /// The DNS record that makes the name POINT HERE — and it is a separate thing from the TXT
    /// record above, which is the trap this field exists to close. Until 2026-09-21 the panel
    /// asked for the TXT and nothing else, so a tenant could prove they owned a hostname that went
    /// on resolving to their old web host, and the first anybody heard of it was a certificate
    /// that could not be issued for a domain nobody could reach.
    /// </summary>
    public string? RoutingRecordName { get; init; }
    public string? RoutingRecordValue { get; init; }

    /// <summary>
    /// Whether the domain currently resolves to the same address as the platform's own host.
    /// Null means the check could not be made (no answer, or the platform host itself would not
    /// resolve), which is NOT the same as false and must not be shown as a problem.
    ///
    /// IT WARNS, IT NEVER REFUSES. DNS propagates for up to a day, a split-horizon or CDN answer
    /// can be legitimately different, and the real gate is the certificate step, which fails with
    /// its own reason. Refusing on a stale lookup would block a tenant who had done everything
    /// right twenty minutes ago.
    /// </summary>
    public bool? PointsHere { get; init; }

    /// <summary>What the routing check saw, in a sentence, when <see cref="PointsHere"/> is false.</summary>
    public string? RoutingHint { get; init; }

    public DateTime? VerifiedAt { get; init; }
    /// <summary>
    /// When the domain was put on this server's certificate and started being served.
    ///
    /// THERE IS NO RENEWAL DATE HERE ON PURPOSE, and it stayed absent when per-domain issuance
    /// came back. A subdomain tenant is served by the host's own shared certificate, which is
    /// maintained outside this application entirely; a tenant on their own domain is served by one
    /// this application asked for but does not renew — certbot's own timer does, on the box. In
    /// both cases a date here would be a guess and an implied promise that Q-Mgr is watching it.
    /// The server log carries the certificate's real expiry at the moment a domain is brought live.
    /// </summary>
    public DateTime? ServingSince { get; init; }

    public int Attempts { get; init; }

    /// <summary>
    /// What failed, naming the STEP. "We could not find the DNS record yet" sends somebody to
    /// their registrar; "the certificate could not be issued" sends them to us. A single "failed"
    /// would send them to the wrong place, which is the whole reason this is a sentence and not a
    /// boolean.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>True once the attempt counter has run out; a human has to ask for another try.</summary>
    public bool GaveUp { get; init; }
}

public enum CustomDomainState
{
    /// <summary>Nothing claimed.</summary>
    None = 0,
    /// <summary>Claimed, TXT record not yet found.</summary>
    Pending = 1,
    /// <summary>TXT matched; no server block has been written, so nothing routes yet.</summary>
    Verified = 2,
    /// <summary>Certificate issued and nginx is serving it. This is the only state that routes.</summary>
    Live = 3
}

public record RequestCustomDomainRequest
{
    public string Domain { get; init; } = string.Empty;
}

/// <summary>
/// What a browser arriving on a host gets to look like, before anybody has signed in.
/// Anonymous, and deliberately narrow: no slug, no contact details, no counts.
/// </summary>
public record TenantHostBrandingDto
{
    /// <summary>
    /// False for the platform's own host and for ANY host that is not a live tenant domain.
    /// An unverified or half-configured host must be indistinguishable from the platform host —
    /// a mistyped DNS record cannot be allowed to half-brand a sign-in page.
    /// </summary>
    public bool Resolved { get; init; }

    public string? BrandName { get; init; }
    public string? LogoUrl { get; init; }
    public string? FaviconUrl { get; init; }
    public string? PrimaryColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? AccentColor { get; init; }

    /// <summary>Resolved server-side. The client is never the judge of what it may remove.</summary>
    public bool AttributionRemoved { get; init; }
}
