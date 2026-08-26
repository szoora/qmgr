using Microsoft.EntityFrameworkCore;
using QMgr.API.Domain.Entities;
using QMgr.Application.Tenant;
using QMgr.Domain.Constants;
using QMgr.Domain.Entities.Billing;
using QMgr.Domain.Entities.Content;
using QMgr.Domain.Entities.Identity;
using QMgr.Domain.Entities.Integration;
using QMgr.Domain.Entities.Notification;
using QMgr.Domain.Entities.Organization;
using QMgr.Domain.Entities.Platform;
using QMgr.Domain.Entities.Marketing;
using QMgr.Domain.Entities.Queue;
using QMgr.Domain.Entities.Visitor;

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
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<Visitor> Visitors => Set<Visitor>();
    public DbSet<VisitorProfile> VisitorProfiles => Set<VisitorProfile>();
    public DbSet<VisitorPass> VisitorPasses => Set<VisitorPass>();

    #endregion

    #region Marketing

    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<BroadcastRecipient> BroadcastRecipients => Set<BroadcastRecipient>();
    public DbSet<BroadcastAttachment> BroadcastAttachments => Set<BroadcastAttachment>();

    #endregion

    #region Content

    public DbSet<MediaContent> MediaContents => Set<MediaContent>();
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
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<AdImpression> AdImpressions => Set<AdImpression>();

    #endregion

    #region Platform

    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();
    public DbSet<PlatformConfiguration> PlatformConfigurations => Set<PlatformConfiguration>();
    public DbSet<PlatformSpotifyConnection> PlatformSpotifyConnections => Set<PlatformSpotifyConnection>();

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

        modelBuilder.Entity<Quote>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        // Billing entities - filter by OrganizationId
        modelBuilder.Entity<Subscription>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<Invoice>()
            .HasQueryFilter(e => !TenantIsolationEnabled || e.OrganizationId == CurrentOrganizationId);

        modelBuilder.Entity<Payment>()
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
            entity.HasOne(e => e.Plan)
                .WithMany(p => p.Subscriptions)
                .HasForeignKey(e => e.PlanId)
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

        return base.SaveChangesAsync(cancellationToken);
    }
}
