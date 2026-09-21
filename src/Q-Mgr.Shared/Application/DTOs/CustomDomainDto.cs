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

    /// <summary>The DNS record the tenant has to create. Null once there is nothing left to prove.</summary>
    public string? VerificationRecordName { get; init; }
    public string? VerificationRecordValue { get; init; }

    public DateTime? VerifiedAt { get; init; }
    /// <summary>
    /// When the domain was put on this server's certificate and started being served.
    ///
    /// THERE IS NO RENEWAL DATE HERE ON PURPOSE. Q-Mgr issues no certificate: the deployment host
    /// carries one, shared with the other applications on it and maintained outside this
    /// application, so a renewal date shown here would be both a guess and an implied promise that
    /// this application is watching it. The server log carries the certificate's real expiry at
    /// the moment a domain is brought live.
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
    /// <summary>TXT matched; the certificate has not been issued, so nothing routes yet.</summary>
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
