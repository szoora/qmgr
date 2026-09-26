using Microsoft.EntityFrameworkCore;
using QMgr.API.Domain.Entities;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Entities.Docs;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Entities.Marketing;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Entities.Visitor;
using QMgr.Domain.Entities.Welfare;
using QMgr.Domain.Identity;

namespace QMgr.Infrastructure.Data;

public class QMgrDbContext : DbContext
{
    private readonly ITenantContextAccessor? _tenantContextAccessor;

    public QMgrDbContext(DbContextOptions<QMgrDbContext> options) : base(options)
    {
    }

    public QMgrDbContext(DbContextOptions<QMgrDbContext> options, ITenantContextAccessor tenantContextAccessor)
        : base(options)
    {
        _tenantContextAccessor = tenantContextAccessor;
    }

    #region Organization

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<BranchSettings> BranchSettings => Set<BranchSettings>();

    #endregion

    #region Queue

    public DbSet<ServiceType> ServiceTypes => Set<ServiceType>();
    public DbSet<Counter> Counters => Set<Counter>();
    public DbSet<CounterServiceType> CounterServiceTypes => Set<CounterServiceType>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<TokenHistory> TokenHistories => Set<TokenHistory>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<FeedbackQuestion> FeedbackQuestions => Set<FeedbackQuestion>();
    public DbSet<Visitor> Visitors => Set<Visitor>();
    public DbSet<VisitorProfile> VisitorProfiles => Set<VisitorProfile>();
    public DbSet<VisitorPass> VisitorPasses => Set<VisitorPass>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<StudentGuardian> StudentGuardians => Set<StudentGuardian>();
    public DbSet<StudentFlag> StudentFlags => Set<StudentFlag>();
    public DbSet<ClassTeacherAssignment> ClassTeacherAssignments => Set<ClassTeacherAssignment>();
    public DbSet<RosterImportJob> RosterImportJobs => Set<RosterImportJob>();
    public DbSet<RosterImportJobEntry> RosterImportJobEntries => Set<RosterImportJobEntry>();

    #endregion

    #region Welfare

    public DbSet<WelfareCategory> WelfareCategories => Set<WelfareCategory>();
    public DbSet<WelfareRecord> WelfareRecords => Set<WelfareRecord>();
    public DbSet<WelfareAttachment> WelfareAttachments => Set<WelfareAttachment>();
    public DbSet<WelfareNote> WelfareNotes => Set<WelfareNote>();
    public DbSet<WelfareNotification> WelfareNotifications => Set<WelfareNotification>();

    #endregion

    #region Staff Performance

    // Department, PerformanceParameter, StaffNotice and ActivityEvent are organization-scoped and
    // carry a tenant query filter below. StaffDuty, StaffPerformanceRecord (+ notes, attachments)
    // and StaffAppraisal are branch-scoped like the welfare tables and are NOT filtered: every
    // controller action reaching one by ID calls VerifyBranchOwnership explicitly.
    public DbSet<QMgr.Domain.Entities.Staff.Department> Departments => Set<QMgr.Domain.Entities.Staff.Department>();
    public DbSet<QMgr.Domain.Entities.Staff.Subject> Subjects => Set<QMgr.Domain.Entities.Staff.Subject>();
    public DbSet<QMgr.Domain.Entities.Staff.PerformanceParameter> PerformanceParameters => Set<QMgr.Domain.Entities.Staff.PerformanceParameter>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffDuty> StaffDuties => Set<QMgr.Domain.Entities.Staff.StaffDuty>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffDutyReport> StaffDutyReports => Set<QMgr.Domain.Entities.Staff.StaffDutyReport>();
    public DbSet<QMgr.Domain.Entities.Staff.TeachingPlan> TeachingPlans => Set<QMgr.Domain.Entities.Staff.TeachingPlan>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffDutyReportNote> StaffDutyReportNotes => Set<QMgr.Domain.Entities.Staff.StaffDutyReportNote>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffDutyReportAttachment> StaffDutyReportAttachments => Set<QMgr.Domain.Entities.Staff.StaffDutyReportAttachment>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffMinuteAction> StaffMinuteActions => Set<QMgr.Domain.Entities.Staff.StaffMinuteAction>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffConfigRequest> StaffConfigRequests => Set<QMgr.Domain.Entities.Staff.StaffConfigRequest>();
    public DbSet<QMgr.Domain.Entities.Staff.Timetable> Timetables => Set<QMgr.Domain.Entities.Staff.Timetable>();
    public DbSet<QMgr.Domain.Entities.Staff.TimetableLesson> TimetableLessons => Set<QMgr.Domain.Entities.Staff.TimetableLesson>();
    public DbSet<QMgr.Domain.Entities.Staff.TimetableLessonException> TimetableLessonExceptions => Set<QMgr.Domain.Entities.Staff.TimetableLessonException>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffPerformanceRecord> StaffPerformanceRecords => Set<QMgr.Domain.Entities.Staff.StaffPerformanceRecord>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffPerformanceNote> StaffPerformanceNotes => Set<QMgr.Domain.Entities.Staff.StaffPerformanceNote>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffPerformanceAttachment> StaffPerformanceAttachments => Set<QMgr.Domain.Entities.Staff.StaffPerformanceAttachment>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffAppraisal> StaffAppraisals => Set<QMgr.Domain.Entities.Staff.StaffAppraisal>();
    public DbSet<QMgr.Domain.Entities.Staff.StaffNotice> StaffNotices => Set<QMgr.Domain.Entities.Staff.StaffNotice>();
    public DbSet<QMgr.Domain.Entities.Audit.ActivityEvent> ActivityEvents => Set<QMgr.Domain.Entities.Audit.ActivityEvent>();

    // School calendar (2026-09-23): organization-scoped, filtered below like StaffNotice.
    public DbSet<QMgr.Domain.Entities.Calendar.SchoolEvent> SchoolEvents => Set<QMgr.Domain.Entities.Calendar.SchoolEvent>();

    #endregion

    #region Marketing

    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<BroadcastRecipient> BroadcastRecipients => Set<BroadcastRecipient>();
    public DbSet<BroadcastAttachment> BroadcastAttachments => Set<BroadcastAttachment>();

    #endregion

    #region Content

    public DbSet<MediaContent> MediaContents => Set<MediaContent>();
    public DbSet<DocumentShare> DocumentShares => Set<DocumentShare>();
    public DbSet<DocumentShareEvent> DocumentShareEvents => Set<DocumentShareEvent>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();
    public DbSet<Display> Displays => Set<Display>();
    public DbSet<DisplayZone> DisplayZones => Set<DisplayZone>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignImpression> CampaignImpressions => Set<CampaignImpression>();

    #endregion

    #region Identity

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>
    /// One row per signed-in mobile handset. See <see cref="UserDeviceSession"/> for why the
    /// browser's single <c>User.RefreshToken</c> could not be stretched to cover this.
    /// </summary>
    public DbSet<UserDeviceSession> UserDeviceSessions => Set<UserDeviceSession>();
    public DbSet<RegistrationAttempt> RegistrationAttempts => Set<RegistrationAttempt>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    #endregion

    #region Integration

    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<ApiLog> ApiLogs => Set<ApiLog>();
    public DbSet<WebhookOutgoing> WebhooksOutgoing => Set<WebhookOutgoing>();

    #endregion

    #region Notifications

    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    #endregion

    #region Billing (SaaS)

    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OrganizationModule> OrganizationModules => Set<OrganizationModule>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<AdImpression> AdImpressions => Set<AdImpression>();

    #endregion

    #region Platform

    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();

    // ── Tenant lifecycle and purge ────────────────────────────────────────────────────────────
    // All three are PLATFORM-owned and outlive the organization they are about: the last row
    // written to each is written when there is no organization left to point at. None carries
    // personal data — TenantTombstone holds two one-way hashes and some counts, and that is what
    // makes keeping it after a purge honest. See TenantDataManifest.
    public DbSet<TenantLifecycleEvent> TenantLifecycleEvents => Set<TenantLifecycleEvent>();
    public DbSet<TenantTombstone> TenantTombstones => Set<TenantTombstone>();
    public DbSet<TenantPurgeCertificate> TenantPurgeCertificates => Set<TenantPurgeCertificate>();
    public DbSet<PlatformConfiguration> PlatformConfigurations => Set<PlatformConfiguration>();
    public DbSet<PlatformSpotifyConnection> PlatformSpotifyConnections => Set<PlatformSpotifyConnection>();

    #endregion

    #region Docs

    // No tenant query filter — platform-owned content, same as the Platform region above.
    public DbSet<DocArticle> DocArticles => Set<DocArticle>();

    #endregion

    /// <summary>
    /// Get the current organization ID from tenant context (for query filters)
    /// </summary>
    private Guid CurrentOrganizationId =>
        _tenantContextAccessor?.TenantContext?.OrganizationId ?? Guid.Empty;

    /// <summary>
    /// Whether tenant isolation is enabled (tenant context is resolved).
    /// Super Admin's JWT still carries an org_id (the Platform org's), so without this
    /// check every query against a tenant-filtered DbSet would silently be scoped to just
    /// that org for Super Admin — even though controllers throughout the app explicitly
    /// branch on "is Super Admin -> no filter" and assume this ORM-level filter isn't
    /// fighting them underneath. Keep this the single place that grants the bypass.
    /// </summary>
    private bool TenantIsolationEnabled =>
        _tenantContextAccessor?.TenantContext?.IsResolved == true &&
        _tenantContextAccessor.TenantContext.OrganizationId != Guid.Empty &&
        !RoleCodes.IsSuperAdmin(_tenantContextAccessor.TenantContext.UserRole);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Set default schema
        modelBuilder.HasDefaultSchema("qmgr");

        // Apply entity configurations
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(QMgrDbContext).Assembly);

        // Configure global query filters for multi-tenancy
        // These filters ensure tenant data isolation for shared schema
        ConfigureTenantQueryFilters(modelBuilder);

        // Configure billing entities
        ConfigureBillingEntities(modelBuilder);

        // Configure RBAC entities
        ConfigureRbacEntities(modelBuilder);
    }

    private void ConfigureTenantQueryFilters(ModelBuilder modelBuilder)
    {
        // Organization-scoped entities - filter by OrganizationId
        modelBuilder.Entity<User>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Add matching filter for UserSession (child of User)
        modelBuilder.Entity<UserSession>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.User.OrganizationId == CurrentOrganizationId);

        // A mobile device session. Filtered on its OWN denormalised column rather than through the
        // user, because the redeem path runs with no tenant context at all — see
        // DeviceSessionService, which reaches these rows with IgnoreQueryFilters for that reason.
        modelBuilder.Entity<UserDeviceSession>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<ApiClient>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Add matching filter for WebhookOutgoing (child of ApiClient)
        modelBuilder.Entity<WebhookOutgoing>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.ApiClient.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<NotificationSettings>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<MediaContent>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Add matching filter for PlaylistItem (child of MediaContent)
        modelBuilder.Entity<PlaylistItem>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.MediaContent.OrganizationId == CurrentOrganizationId);

        // Share links and their events follow the document they belong to. The PUBLIC share
        // endpoints run with no tenant resolved (an anonymous viewer has none), so the filter is
        // off there and the lookup is by slug hash alone — which is the intended shape.
        modelBuilder.Entity<DocumentShare>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.MediaContent!.OrganizationId == CurrentOrganizationId);
        modelBuilder.Entity<DocumentShareEvent>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.Share!.MediaContent!.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<Quote>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Staff Performance: the organization-scoped four. UploadAuthorizer and the Hangfire sweeps
        // use IgnoreQueryFilters where they must read across tenants.
        modelBuilder.Entity<QMgr.Domain.Entities.Staff.Department>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);
        modelBuilder.Entity<QMgr.Domain.Entities.Staff.PerformanceParameter>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);
        modelBuilder.Entity<QMgr.Domain.Entities.Staff.StaffNotice>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);
        modelBuilder.Entity<QMgr.Domain.Entities.Calendar.SchoolEvent>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);
        modelBuilder.Entity<QMgr.Domain.Entities.Audit.ActivityEvent>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);
        // Duty rota plan (2026-09-17): subjects are organization-scoped like departments.
        modelBuilder.Entity<QMgr.Domain.Entities.Staff.Subject>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Billing entities - filter by OrganizationId
        modelBuilder.Entity<Subscription>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<Invoice>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<Payment>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<OrganizationModule>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<UsageRecord>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<AdImpression>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Note: Branch-scoped entities (Token, Counter, ServiceType, etc.) are filtered
        // through their Branch relationship, which is already scoped to Organization
    }

    private void ConfigureBillingEntities(ModelBuilder modelBuilder)
    {
        // SubscriptionPlan configuration
        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.ToTable("subscription_plans");
            entity.HasIndex(e => e.Code).IsUnique();
            entity.Property(e => e.Features).HasColumnType("jsonb");
        });

        // Subscription configuration
        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions");
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.Subscriptions)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Invoice configuration
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.ToTable("invoices");
            entity.HasIndex(e => e.InvoiceNumber).IsUnique();
            entity.Property(e => e.LineItems).HasColumnType("jsonb");
            entity.Property(e => e.BillingAddress).HasColumnType("jsonb");
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Subscription)
                .WithMany(s => s.Invoices)
                .HasForeignKey(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // OrganizationModule configuration — the modular subscription system's core join table:
        // an org can hold many of these (one per purchased module), unlike the legacy single
        // Subscription.PlanId FK it supersedes.
        modelBuilder.Entity<OrganizationModule>(entity =>
        {
            entity.ToTable("organization_modules");
            entity.HasIndex(e => new { e.OrganizationId, e.ModuleId }).IsUnique();
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Module)
                .WithMany()
                .HasForeignKey(e => e.ModuleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Payment configuration
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("payments");
            entity.HasIndex(e => e.ReferenceId).IsUnique();
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Subscription)
                .WithMany(s => s.Payments)
                .HasForeignKey(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.Invoice)
                .WithMany(i => i.Payments)
                .HasForeignKey(e => e.InvoiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // UsageRecord configuration
        modelBuilder.Entity<UsageRecord>(entity =>
        {
            entity.ToTable("usage_records");
            entity.HasIndex(e => new { e.OrganizationId, e.Year, e.Month }).IsUnique();
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.UsageRecords)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AdImpression configuration
        modelBuilder.Entity<AdImpression>(entity =>
        {
            entity.ToTable("ad_impressions");
            entity.HasIndex(e => new { e.OrganizationId, e.Date, e.AdSlot });
            entity.HasOne(e => e.Organization)
                .WithMany(o => o.AdImpressions)
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Branch)
                .WithMany()
                .HasForeignKey(e => e.BranchId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Display)
                .WithMany()
                .HasForeignKey(e => e.DisplayId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Update Organization configuration for new fields
        // Deliberately NO foreign key from any of these to Organization: each one outlives the
        // organization it names, and the most important row in each is written when there is
        // nothing left to point at.
        modelBuilder.Entity<TenantLifecycleEvent>(entity =>
        {
            entity.ToTable("tenant_lifecycle_events");
            entity.HasIndex(e => new { e.OrganizationId, e.OccurredAt });
            entity.Property(e => e.OrganizationName).HasMaxLength(300);
            entity.Property(e => e.Actor).HasMaxLength(200);
            entity.Property(e => e.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<TenantTombstone>(entity =>
        {
            entity.ToTable("tenant_tombstones");
            // One per purged organization, and the restore hook matches on it.
            entity.HasIndex(e => e.OrganizationId).IsUnique();
            // The registration guard looks up a returning sign-up by these two hashes.
            entity.HasIndex(e => e.EmailDomainHash);
            entity.HasIndex(e => e.NameKeyHash);
            entity.Property(e => e.EmailDomainHash).HasMaxLength(64);
            entity.Property(e => e.NameKeyHash).HasMaxLength(64);
            entity.Property(e => e.PurgedBy).HasMaxLength(200);
            entity.Property(e => e.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<TenantPurgeCertificate>(entity =>
        {
            entity.ToTable("tenant_purge_certificates");
            entity.HasIndex(e => new { e.OrganizationId, e.PurgedAt });
            entity.Property(e => e.PurgedBy).HasMaxLength(200);
            entity.Property(e => e.RowsDeletedJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasIndex(e => e.Slug).IsUnique().HasDatabaseName("idx_organizations_slug");
            entity.HasIndex(e => e.CustomDomain).IsUnique().HasDatabaseName("idx_organizations_custom_domain")
                .HasFilter("\"CustomDomain\" IS NOT NULL");
            entity.HasOne(e => e.Subscription)
                .WithOne()
                .HasForeignKey<Organization>(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private void ConfigureRbacEntities(ModelBuilder modelBuilder)
    {
        // Role configuration
        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles");
            entity.HasIndex(e => new { e.OrganizationId, e.Code })
                .IsUnique()
                .HasDatabaseName("idx_roles_org_code");
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Color).HasMaxLength(20);
            entity.Property(e => e.Icon).HasMaxLength(50);

            entity.HasOne(e => e.Organization)
                .WithMany()
                .HasForeignKey(e => e.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Permission configuration
        modelBuilder.Entity<Permission>(entity =>
        {
            entity.ToTable("permissions");
            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("idx_permissions_code");
            entity.Property(e => e.Code).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Category).HasMaxLength(100).IsRequired();
        });

        // RolePermission junction table configuration
        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(e => new { e.RoleId, e.PermissionId });

            entity.HasOne(e => e.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Permission)
                .WithMany(p => p.RolePermissions)
                .HasForeignKey(e => e.PermissionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // User-Role relationship
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasOne(e => e.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Restrict); // Prevent deleting roles with users
        });

        // Query filter for roles: show system roles (OrganizationId = null) + tenant roles
        modelBuilder.Entity<Role>()
            .HasQueryFilter(e => !TenantIsolationEnabled ||
                e.OrganizationId == null ||
                e.OrganizationId == CurrentOrganizationId);

        // Add matching filter for RolePermission (child of Role)
        modelBuilder.Entity<RolePermission>()
            .HasQueryFilter(e => !TenantIsolationEnabled ||
                e.Role.OrganizationId == null ||
                e.Role.OrganizationId == CurrentOrganizationId);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Domain.Common.BaseEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        ApplyIdentityNormalization();

        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Keeps the canonical duplicate-detection columns in step with the values they are derived
    /// from, for every user and organization written through this context.
    /// </summary>
    /// <remarks>
    /// This lives here rather than at each call site because there are several: self-service
    /// sign-up, an administrator adding a user, and three separate seeders. NormalizedEmail carries
    /// a unique index and starts life as an empty string, so a single site that forgot to populate
    /// it would not merely weaken the duplicate check — the second such row would violate the index
    /// and fail the write outright, which on a fresh install means seeding two demo users breaks
    /// startup. Deriving the values here makes forgetting impossible.
    /// </remarks>
    private void ApplyIdentityNormalization()
    {
        foreach (var entry in ChangeTracker.Entries<Domain.Entities.Identity.User>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;

            var user = entry.Entity;

            // NO ADDRESS AT ALL leaves both columns null — the unique index tolerates any number of
            // those, where a shared empty string would let exactly one person exist without email.
            // Otherwise: falling back to the raw address keeps the column non-empty for anything the
            // normalizer declines to interpret, so the unique index still separates those rows.
            user.NormalizedEmail = string.IsNullOrWhiteSpace(user.Email)
                ? null
                : RegistrationIdentity.NormalizeEmail(user.Email) ?? user.Email.Trim().ToLowerInvariant();
            var newPhone = RegistrationIdentity.NormalizePhone(user.Phone);

            // A confirmation belongs to a NUMBER, not to a person: a changed number is unconfirmed
            // again, whoever changed it. This was one writer's rule (ProfileController) until
            // 2026-09-25 — the portal's contact form, an administrator's edit, the class-teacher
            // card and a re-import all kept the old confirmation on a new number, so SMS password
            // resets went to a number nobody had confirmed. Here it cannot be forgotten. A save that
            // sets the confirmation itself (the verify-code endpoint) is left alone.
            if (entry.State == EntityState.Modified
                && !entry.Property(nameof(Domain.Entities.Identity.User.PhoneVerifiedAt)).IsModified
                && entry.Property(nameof(Domain.Entities.Identity.User.Phone)).IsModified
                && !string.Equals(RegistrationIdentity.NormalizePhone(entry.Property(nameof(Domain.Entities.Identity.User.Phone)).OriginalValue as string),
                                  newPhone, StringComparison.Ordinal))
                user.PhoneVerifiedAt = null;

            user.NormalizedPhone = newPhone;
        }

        foreach (var entry in ChangeTracker.Entries<Domain.Entities.Organization.Organization>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;

            var organization = entry.Entity;
            organization.NormalizedName = RegistrationIdentity.NormalizeOrganizationName(organization.Name);
            organization.NameBlockingKey = RegistrationIdentity.BuildNameBlockingKey(organization.Name);
        }
    }
}
