namespace QMgr.Infrastructure.Data.Purge;

/// <summary>
/// The ONE declared thing in the purge: what each table is. Everything else — which column holds
/// the organization, how a table with no such column reaches one, what order to delete in — is
/// computed from the EF model by <see cref="TenantPurgeModel"/> and needs no maintenance.
///
/// WHY THIS IS A LIST AND THE REST IS NOT. Of 78 entities, 39 carry their own
/// <c>OrganizationId</c> and 39 do not; about six of the second group are genuinely the platform's
/// and the other thirty-odd are tenant data hanging off a parent. A purge written as "delete these
/// tables in this order" would have to encode all thirty-odd join paths AND the safe order, and
/// then be extended correctly by every future feature — and when it drifted nothing would break.
/// The rows would just stay, silently, in a system whose whole promise is that they are gone.
///
/// So the only thing asked of a developer adding a table is one line here, and
/// <see cref="TenantPurgeModel.Validate"/> refuses to start the application until they add it. The
/// same fail-closed shape as <c>UploadAuthorizer.LookUpAsync</c>, where an upload surface nobody
/// classified becomes an unreadable orphan: a new kind fails closed there too.
/// </summary>
public static class TenantDataManifest
{
    /// <summary>
    /// Keyed on the entity's CLR name, because that is what survives a table rename and what a
    /// developer has in front of them. <see cref="TenantPurgeModel"/> resolves it against the model.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TenantDataClass> ByEntity = new Dictionary<string, TenantDataClass>(StringComparer.Ordinal)
    {
        // ── The organization itself ────────────────────────────────────────────────────────────
        // Deleted LAST, and only once everything that points at it has gone. A purged tenant has
        // no row here at all — what survives is a TenantTombstone, which holds no personal data.
        ["Organization"] = TenantDataClass.TenantOwned,

        // ── Tenant-owned: carries its own OrganizationId ───────────────────────────────────────
        ["ActivityEvent"] = TenantDataClass.TenantOwned,
        ["AdImpression"] = TenantDataClass.TenantOwned,
        ["OrganizationModule"] = TenantDataClass.TenantOwned,
        ["Subscription"] = TenantDataClass.TenantOwned,
        ["UsageRecord"] = TenantDataClass.TenantOwned,
        ["MediaContent"] = TenantDataClass.TenantOwned,
        ["Quote"] = TenantDataClass.TenantOwned,
        ["RegistrationAttempt"] = TenantDataClass.TenantOwned,
        ["Role"] = TenantDataClass.TenantOwned,
        ["User"] = TenantDataClass.TenantOwned,
        ["ApiClient"] = TenantDataClass.TenantOwned,
        ["Broadcast"] = TenantDataClass.TenantOwned,
        ["Contact"] = TenantDataClass.TenantOwned,
        ["Notification"] = TenantDataClass.TenantOwned,
        ["NotificationSettings"] = TenantDataClass.TenantOwned,
        ["Branch"] = TenantDataClass.TenantOwned,
        ["Appointment"] = TenantDataClass.TenantOwned,
        ["FeedbackQuestion"] = TenantDataClass.TenantOwned,
        ["Department"] = TenantDataClass.TenantOwned,
        ["PerformanceParameter"] = TenantDataClass.TenantOwned,
        ["StaffAppraisal"] = TenantDataClass.TenantOwned,
        ["StaffDuty"] = TenantDataClass.TenantOwned,
        ["StaffDutyReport"] = TenantDataClass.TenantOwned,
        ["StaffMinuteAction"] = TenantDataClass.TenantOwned,
        // A staff member's own request to teach a class or take a period. Entirely the tenant's, and
        // it carries a named member of staff and their words, so it goes when the tenant goes.
        ["StaffConfigRequest"] = TenantDataClass.TenantOwned,
        ["StaffNotice"] = TenantDataClass.TenantOwned,
        ["StaffPerformanceRecord"] = TenantDataClass.TenantOwned,
        ["Subject"] = TenantDataClass.TenantOwned,
        ["Timetable"] = TenantDataClass.TenantOwned,
        ["Visitor"] = TenantDataClass.TenantOwned,
        ["VisitorPass"] = TenantDataClass.TenantOwned,
        ["VisitorProfile"] = TenantDataClass.TenantOwned,
        ["ClassTeacherAssignment"] = TenantDataClass.TenantOwned,
        ["RosterImportJob"] = TenantDataClass.TenantOwned,
        ["Student"] = TenantDataClass.TenantOwned,
        ["StudentFlag"] = TenantDataClass.TenantOwned,
        ["WelfareCategory"] = TenantDataClass.TenantOwned,
        ["WelfareRecord"] = TenantDataClass.TenantOwned,

        // ── Tenant-derived: reaches the organization through a parent ──────────────────────────
        // The path is COMPUTED. These lines say only "this is the tenant's", never how to find it.
        ["Campaign"] = TenantDataClass.TenantDerived,
        ["CampaignImpression"] = TenantDataClass.TenantDerived,
        ["Display"] = TenantDataClass.TenantDerived,
        ["DisplayZone"] = TenantDataClass.TenantDerived,
        ["DocumentShare"] = TenantDataClass.TenantDerived,
        ["DocumentShareEvent"] = TenantDataClass.TenantDerived,
        ["Playlist"] = TenantDataClass.TenantDerived,
        ["PlaylistItem"] = TenantDataClass.TenantDerived,
        ["RolePermission"] = TenantDataClass.TenantDerived,
        ["UserSession"] = TenantDataClass.TenantDerived,
        ["ApiLog"] = TenantDataClass.TenantDerived,
        ["WebhookOutgoing"] = TenantDataClass.TenantDerived,
        ["BroadcastAttachment"] = TenantDataClass.TenantDerived,
        ["BroadcastRecipient"] = TenantDataClass.TenantDerived,
        ["NotificationLog"] = TenantDataClass.TenantDerived,
        ["BranchSettings"] = TenantDataClass.TenantDerived,
        ["Counter"] = TenantDataClass.TenantDerived,
        ["CounterServiceType"] = TenantDataClass.TenantDerived,
        ["Feedback"] = TenantDataClass.TenantDerived,
        ["ServiceType"] = TenantDataClass.TenantDerived,
        ["Token"] = TenantDataClass.TenantDerived,
        ["TokenHistory"] = TenantDataClass.TenantDerived,
        ["StaffDutyReportAttachment"] = TenantDataClass.TenantDerived,
        ["StaffDutyReportNote"] = TenantDataClass.TenantDerived,
        ["StaffPerformanceAttachment"] = TenantDataClass.TenantDerived,
        ["StaffPerformanceNote"] = TenantDataClass.TenantDerived,
        ["TimetableLesson"] = TenantDataClass.TenantDerived,
        ["RosterImportJobEntry"] = TenantDataClass.TenantDerived,
        ["StudentGuardian"] = TenantDataClass.TenantDerived,
        ["WelfareAttachment"] = TenantDataClass.TenantDerived,
        ["WelfareNote"] = TenantDataClass.TenantDerived,
        ["WelfareNotification"] = TenantDataClass.TenantDerived,

        // ── Platform-owned: shared, and NEVER touched by a tenant purge ────────────────────────
        // Deleting one of these while emptying one tenant breaks every other tenant on the box.
        ["PlatformConfiguration"] = TenantDataClass.PlatformOwned,
        ["PlatformSetting"] = TenantDataClass.PlatformOwned,
        ["PlatformSpotifyConnection"] = TenantDataClass.PlatformOwned,
        ["SubscriptionPlan"] = TenantDataClass.PlatformOwned,   // the module catalogue
        ["Permission"] = TenantDataClass.PlatformOwned,         // the permission catalogue
        ["DocArticle"] = TenantDataClass.PlatformOwned,         // the help centre
        ["TenantTombstone"] = TenantDataClass.PlatformOwned,    // what a purge LEAVES; purging it would be circular
        ["TenantPurgeCertificate"] = TenantDataClass.PlatformOwned,
        ["TenantLifecycleEvent"] = TenantDataClass.PlatformOwned,

        // ── Statutory retention: kept, de-identified, on its own five-year clock ───────────────
        ["Invoice"] = TenantDataClass.StatutoryRetention,
        ["Payment"] = TenantDataClass.StatutoryRetention,
    };

    /// <summary>
    /// Personal data to blank on a <see cref="TenantDataClass.StatutoryRetention"/> row, per entity.
    ///
    /// RETAINED DOES NOT MEAN UNTOUCHED. A retained invoice keeps what a tax audit needs — the
    /// amount, the currency, the dates, an opaque reference — and loses what it does not: the
    /// billing contact's name, their email, their address. That is de-identification in the Data
    /// Protection and Privacy Act's own language, and it is what resolves "leave no trace" against
    /// a five-year statutory retention period.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> DeIdentifyColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Invoice"] = ["BillingEmail", "BillingName", "BillingAddress", "Notes", "FooterText", "StripeInvoiceUrl", "StripePdfUrl"],
        // MobileMoneyPhone is the payer's own number. The card brand and last four stay: they are
        // not a person, and a reconciliation against a bank statement needs them.
        ["Payment"] = ["MobileMoneyPhone", "ErrorMessage"],
    };

    /// <summary>
    /// How long a de-identified financial row is kept before it goes too. Five years, from Uganda's
    /// Tax Procedures Code. Measured from the end of the tax period, so the sweep is generous by
    /// design and counts from the row's own date.
    /// </summary>
    public static readonly TimeSpan StatutoryRetentionPeriod = TimeSpan.FromDays(365 * 5 + 2);
}
