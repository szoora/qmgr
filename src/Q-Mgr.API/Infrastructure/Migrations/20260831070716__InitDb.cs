using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class _InitDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "qmgr");

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_spotify_connections",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpotifyUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "text", nullable: false),
                    RefreshTokenProtected = table.Column<string>(type: "text", nullable: false),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ConnectedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_spotify_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformConfigurations",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformSettings",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsEditable = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    Icon = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_plans",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    MonthlyPriceUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    AnnualPriceUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    MonthlyPriceUgx = table.Column<decimal>(type: "numeric", nullable: false),
                    AnnualPriceUgx = table.Column<decimal>(type: "numeric", nullable: false),
                    StripePriceIdMonthly = table.Column<string>(type: "text", nullable: true),
                    StripePriceIdAnnual = table.Column<string>(type: "text", nullable: true),
                    MaxBranches = table.Column<int>(type: "integer", nullable: false),
                    MaxDisplays = table.Column<int>(type: "integer", nullable: false),
                    MaxUsersPerBranch = table.Column<int>(type: "integer", nullable: false),
                    MaxCountersPerBranch = table.Column<int>(type: "integer", nullable: false),
                    MaxTokensPerMonth = table.Column<int>(type: "integer", nullable: false),
                    MaxApiCallsPerMonth = table.Column<int>(type: "integer", nullable: false),
                    MaxStorageMb = table.Column<int>(type: "integer", nullable: false),
                    Features = table.Column<string>(type: "jsonb", nullable: true),
                    ShowAds = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresDedicatedSchema = table.Column<bool>(type: "boolean", nullable: false),
                    TrialDays = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    Badge = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ad_impressions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdSlot = table.Column<string>(type: "text", nullable: false),
                    AdProvider = table.Column<string>(type: "text", nullable: false),
                    CampaignId = table.Column<string>(type: "text", nullable: true),
                    CreativeId = table.Column<string>(type: "text", nullable: true),
                    AdUnitId = table.Column<string>(type: "text", nullable: true),
                    ImpressionCount = table.Column<int>(type: "integer", nullable: false),
                    ClickCount = table.Column<int>(type: "integer", nullable: false),
                    EstimatedRevenue = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: true),
                    EstimatedViewers = table.Column<int>(type: "integer", nullable: true),
                    AvgDwellTimeSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ad_impressions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_clients",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientSecretHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SystemType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<string[]>(type: "text[]", nullable: true),
                    AllowedBranches = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    RateLimitPerMinute = table.Column<int>(type: "integer", nullable: false),
                    WebhookUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WebhookEvents = table.Column<string[]>(type: "text[]", nullable: true),
                    WebhookSecret = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_logs",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    RequestBody = table.Column<string>(type: "jsonb", nullable: true),
                    ResponseStatus = table.Column<int>(type: "integer", nullable: true),
                    ResponseTimeMs = table.Column<int>(type: "integer", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_logs_api_clients_ApiClientId",
                        column: x => x.ApiClientId,
                        principalSchema: "qmgr",
                        principalTable: "api_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "webhooks_outgoing",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks_outgoing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhooks_outgoing_api_clients_ApiClientId",
                        column: x => x.ApiClientId,
                        principalSchema: "qmgr",
                        principalTable: "api_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "branch_settings",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefaultKioskPrinter = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    KioskTimeBetweenSlides = table.Column<int>(type: "integer", nullable: false),
                    KioskScrollerSpeed = table.Column<int>(type: "integer", nullable: false),
                    KioskSettingsJson = table.Column<string>(type: "jsonb", nullable: true),
                    PreferredPrintMethod = table.Column<int>(type: "integer", nullable: false),
                    PrinterType = table.Column<int>(type: "integer", nullable: false),
                    PrinterName = table.Column<string>(type: "text", nullable: true),
                    PrinterIpAddress = table.Column<string>(type: "text", nullable: true),
                    PrinterPort = table.Column<int>(type: "integer", nullable: false),
                    ThermalPaperWidth = table.Column<int>(type: "integer", nullable: false),
                    PrintLogo = table.Column<bool>(type: "boolean", nullable: false),
                    PrintLogoUrl = table.Column<string>(type: "text", nullable: true),
                    PrintQrCode = table.Column<bool>(type: "boolean", nullable: false),
                    PrintFeedbackUrl = table.Column<bool>(type: "boolean", nullable: false),
                    PrintHeaderText = table.Column<string>(type: "text", nullable: true),
                    PrintFooterText = table.Column<string>(type: "text", nullable: true),
                    PrintFontSize = table.Column<int>(type: "integer", nullable: false),
                    AutoPrintOnTokenCreate = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayTimeBetweenSlides = table.Column<int>(type: "integer", nullable: false),
                    EnableVoiceAnnouncement = table.Column<bool>(type: "boolean", nullable: false),
                    VoiceLanguage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SystemSettingsJson = table.Column<string>(type: "text", nullable: true),
                    DisplayBannerEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayBannerSettingsJson = table.Column<string>(type: "text", nullable: true),
                    EnableSmsNotification = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEmailNotification = table.Column<bool>(type: "boolean", nullable: false),
                    TokenExpiryHours = table.Column<int>(type: "integer", nullable: false),
                    ResetTokenNumbersDaily = table.Column<bool>(type: "boolean", nullable: false),
                    SmsTemplateTokenCreated = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SmsTemplateTokenCalled = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EmailTemplateTokenCreated = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branch_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "branches",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Timezone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "UTC"),
                    OperatingHours = table.Column<string>(type: "jsonb", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaigns_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "displays",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DisplayType = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Resolution = table.Column<string>(type: "jsonb", nullable: true),
                    Orientation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "landscape"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "offline"),
                    LastHeartbeat = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_displays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_displays_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlists",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ScheduleType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "always"),
                    Schedule = table.Column<string>(type: "jsonb", nullable: true),
                    TransitionType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "fade"),
                    DefaultDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    Loop = table.Column<bool>(type: "boolean", nullable: false),
                    Shuffle = table.Column<bool>(type: "boolean", nullable: false),
                    SpotifyPlaylistId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SpotifyPlaylistName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_playlists_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_types",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Prefix = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    AverageServiceTimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IconUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Color = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_types", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_types_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "display_zones",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ZoneType = table.Column<int>(type: "integer", nullable: false),
                    PositionX = table.Column<int>(type: "integer", nullable: false),
                    PositionY = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    ZIndex = table.Column<int>(type: "integer", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_display_zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_display_zones_displays_DisplayId",
                        column: x => x.DisplayId,
                        principalSchema: "qmgr",
                        principalTable: "displays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_display_zones_playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalSchema: "qmgr",
                        principalTable: "playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "broadcast_attachments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BroadcastId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcast_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "broadcast_recipients",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BroadcastId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcast_recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "broadcasts",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MessageBody = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    AudienceTagFilter = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalRecipients = table.Column<int>(type: "integer", nullable: false),
                    SentCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcasts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_broadcasts_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "campaign_impressions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaContentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_impressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaign_impressions_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "qmgr",
                        principalTable: "campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contacts",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TelegramChatId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Tags = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    OptedOut = table.Column<bool>(type: "boolean", nullable: false),
                    OptedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OptOutToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contacts_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "counter_service_types",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_counter_service_types", x => x.Id);
                    table.ForeignKey(
                        name: "FK_counter_service_types_service_types_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalSchema: "qmgr",
                        principalTable: "service_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "counters",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_counters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_counters_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tokens",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisplayNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CustomerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CustomerPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CustomerEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExternalSystem = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServiceStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServiceCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EstimatedWaitMinutes = table.Column<int>(type: "integer", nullable: true),
                    ActualWaitMinutes = table.Column<int>(type: "integer", nullable: true),
                    ServiceDurationMinutes = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tokens_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tokens_counters_CounterId",
                        column: x => x.CounterId,
                        principalSchema: "qmgr",
                        principalTable: "counters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tokens_service_types_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalSchema: "qmgr",
                        principalTable: "service_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "feedbacks",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CounterId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    FeedbackCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CustomerPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CustomerEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TokenDisplayNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ServiceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Response = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RespondedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedbacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedbacks_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_feedbacks_counters_CounterId",
                        column: x => x.CounterId,
                        principalSchema: "qmgr",
                        principalTable: "counters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_feedbacks_service_types_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalSchema: "qmgr",
                        principalTable: "service_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_feedbacks_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "qmgr",
                        principalTable: "tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    Total = table.Column<decimal>(type: "numeric", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvoiceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StripeInvoiceId = table.Column<string>(type: "text", nullable: true),
                    StripeInvoiceUrl = table.Column<string>(type: "text", nullable: true),
                    StripePdfUrl = table.Column<string>(type: "text", nullable: true),
                    LineItems = table.Column<string>(type: "jsonb", nullable: true),
                    BillingEmail = table.Column<string>(type: "text", nullable: true),
                    BillingName = table.Column<string>(type: "text", nullable: true),
                    BillingAddress = table.Column<string>(type: "jsonb", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    FooterText = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "media_content",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ContentType = table.Column<int>(type: "integer", nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StorageType = table.Column<int>(type: "integer", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FileUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Dimensions = table.Column<string>(type: "jsonb", nullable: true),
                    TextContent = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string[]>(type: "text[]", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_content", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "playlist_items",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaContentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Conditions = table.Column<string>(type: "jsonb", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_playlist_items_campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "qmgr",
                        principalTable: "campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_playlist_items_media_content_MediaContentId",
                        column: x => x.MediaContentId,
                        principalSchema: "qmgr",
                        principalTable: "media_content",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_items_playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalSchema: "qmgr",
                        principalTable: "playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Recipient = table.Column<string>(type: "text", nullable: false),
                    RequestPayload = table.Column<string>(type: "text", nullable: true),
                    ResponsePayload = table.Column<string>(type: "text", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IconClass = table.Column<string>(type: "text", nullable: true),
                    ActionUrl = table.Column<string>(type: "text", nullable: true),
                    MetaData = table.Column<string>(type: "text", nullable: true),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredVia = table.Column<int>(type: "integer", nullable: false),
                    SmsSent = table.Column<bool>(type: "boolean", nullable: false),
                    SmsSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailSent = table.Column<bool>(type: "boolean", nullable: false),
                    EmailSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PushSent = table.Column<bool>(type: "boolean", nullable: false),
                    PushSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SmsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmsGatewayUrl = table.Column<string>(type: "text", nullable: true),
                    SmsApiKey = table.Column<string>(type: "text", nullable: true),
                    SmsUsername = table.Column<string>(type: "text", nullable: true),
                    SmsPassword = table.Column<string>(type: "text", nullable: true),
                    SmsSenderId = table.Column<string>(type: "text", nullable: true),
                    SmsCustomerId = table.Column<string>(type: "text", nullable: true),
                    SmsLeadTokens = table.Column<int>(type: "integer", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpHost = table.Column<string>(type: "text", nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    SmtpUsername = table.Column<string>(type: "text", nullable: true),
                    SmtpPassword = table.Column<string>(type: "text", nullable: true),
                    EmailFromAddress = table.Column<string>(type: "text", nullable: true),
                    EmailFromName = table.Column<string>(type: "text", nullable: true),
                    TelegramEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TelegramBotToken = table.Column<string>(type: "text", nullable: true),
                    WhatsAppEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    WhatsAppPhoneNumberId = table.Column<string>(type: "text", nullable: true),
                    WhatsAppAccessToken = table.Column<string>(type: "text", nullable: true),
                    InAppEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    InAppPlaySound = table.Column<bool>(type: "boolean", nullable: false),
                    InAppRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    PushEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FirebaseProjectId = table.Column<string>(type: "text", nullable: true),
                    FirebasePrivateKey = table.Column<string>(type: "text", nullable: true),
                    FirebaseClientEmail = table.Column<string>(type: "text", nullable: true),
                    SmsTokenCreatedTemplate = table.Column<string>(type: "text", nullable: true),
                    SmsTokenCalledTemplate = table.Column<string>(type: "text", nullable: true),
                    SmsReminderTemplate = table.Column<string>(type: "text", nullable: true),
                    EmailTokenCreatedSubject = table.Column<string>(type: "text", nullable: true),
                    EmailTokenCreatedTemplate = table.Column<string>(type: "text", nullable: true),
                    EmailTokenCalledSubject = table.Column<string>(type: "text", nullable: true),
                    EmailTokenCalledTemplate = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BrandName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Website = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: true),
                    PrimaryColor = table.Column<string>(type: "text", nullable: true),
                    SecondaryColor = table.Column<string>(type: "text", nullable: true),
                    AccentColor = table.Column<string>(type: "text", nullable: true),
                    FaviconUrl = table.Column<string>(type: "text", nullable: true),
                    WhitelabelEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayTheme = table.Column<string>(type: "text", nullable: false),
                    IndustryType = table.Column<int>(type: "integer", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    CustomDomain = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    SchemaName = table.Column<string>(type: "text", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StripeCustomerId = table.Column<string>(type: "text", nullable: true),
                    TrialEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BillingEmail = table.Column<string>(type: "text", nullable: true),
                    BillingPhone = table.Column<string>(type: "text", nullable: true),
                    PreferredCurrency = table.Column<string>(type: "text", nullable: false),
                    OnboardingCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    OnboardingStep = table.Column<int>(type: "integer", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Motivational"),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Author = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quotes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Icon = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_roles_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roster_import_jobs",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RowsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    ProcessedRows = table.Column<int>(type: "integer", nullable: false),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedCount = table.Column<int>(type: "integer", nullable: false),
                    DuplicateCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_import_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_roster_import_jobs_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_roster_import_jobs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "students",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StudentCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClassName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.Id);
                    table.ForeignKey(
                        name: "FK_students_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_students_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    BillingCycle = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CurrentPeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrialEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextBillingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StripeSubscriptionId = table.Column<string>(type: "text", nullable: true),
                    StripeCustomerId = table.Column<string>(type: "text", nullable: true),
                    StripePaymentMethodId = table.Column<string>(type: "text", nullable: true),
                    MobileMoneyPhone = table.Column<string>(type: "text", nullable: true),
                    PreferredPaymentMethod = table.Column<int>(type: "integer", nullable: false),
                    MaxBranchesOverride = table.Column<int>(type: "integer", nullable: true),
                    MaxTokensOverride = table.Column<int>(type: "integer", nullable: true),
                    MaxApiCallsOverride = table.Column<int>(type: "integer", nullable: true),
                    MaxUsersOverride = table.Column<int>(type: "integer", nullable: true),
                    MaxDisplaysOverride = table.Column<int>(type: "integer", nullable: true),
                    MaxStorageOverride = table.Column<int>(type: "integer", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    CancelAtPeriodEnd = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_subscriptions_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscriptions_subscription_plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "qmgr",
                        principalTable: "subscription_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_records",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    TokensCreated = table.Column<int>(type: "integer", nullable: false),
                    TokensServed = table.Column<int>(type: "integer", nullable: false),
                    TokensCancelled = table.Column<int>(type: "integer", nullable: false),
                    ApiCalls = table.Column<int>(type: "integer", nullable: false),
                    WebhookDeliveries = table.Column<int>(type: "integer", nullable: false),
                    ActiveUsers = table.Column<int>(type: "integer", nullable: false),
                    ActiveBranches = table.Column<int>(type: "integer", nullable: false),
                    ActiveCounters = table.Column<int>(type: "integer", nullable: false),
                    StorageUsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    SmsMessagesSent = table.Column<int>(type: "integer", nullable: false),
                    EmailsSent = table.Column<int>(type: "integer", nullable: false),
                    PushNotificationsSent = table.Column<int>(type: "integer", nullable: false),
                    DisplayViews = table.Column<int>(type: "integer", nullable: false),
                    AdImpressions = table.Column<int>(type: "integer", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinalizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usage_records_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visitor_passes",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MaxVisitors = table.Column<int>(type: "integer", nullable: false),
                    CurrentVisitors = table.Column<int>(type: "integer", nullable: false),
                    TokenId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visitor_passes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visitor_passes_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_visitor_passes_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "visitor_profiles",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Company = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IdNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PhotoUrl = table.Column<string>(type: "text", nullable: true),
                    NormalizedPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    NormalizedIdNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsWatchlisted = table.Column<bool>(type: "boolean", nullable: false),
                    WatchlistReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visitor_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visitor_profiles_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "qmgr",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.RoleId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "qmgr",
                        principalTable: "permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "qmgr",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EmployeeNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedBranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedCounterId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastLogin = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefreshToken = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RefreshTokenExpiry = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedLoginAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockoutEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_users_branches_AssignedBranchId",
                        column: x => x.AssignedBranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_users_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_users_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "qmgr",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roster_import_job_entries",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RosterImportJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowNumber = table.Column<int>(type: "integer", nullable: false),
                    StudentCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StudentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    GuardianName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuardianProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roster_import_job_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_roster_import_job_entries_roster_import_jobs_RosterImportJo~",
                        column: x => x.RosterImportJobId,
                        principalSchema: "qmgr",
                        principalTable: "roster_import_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    PaymentMethod = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReferenceId = table.Column<string>(type: "text", nullable: false),
                    ExternalReferenceId = table.Column<string>(type: "text", nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "text", nullable: true),
                    StripeChargeId = table.Column<string>(type: "text", nullable: true),
                    MobileMoneyTransactionId = table.Column<string>(type: "text", nullable: true),
                    MobileMoneyPhone = table.Column<string>(type: "text", nullable: true),
                    MobileMoneyChannel = table.Column<string>(type: "text", nullable: true),
                    CardLast4 = table.Column<string>(type: "text", nullable: true),
                    CardBrand = table.Column<string>(type: "text", nullable: true),
                    InitiatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    RefundAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    RefundReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payments_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "qmgr",
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_payments_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payments_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "qmgr",
                        principalTable: "subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "student_guardians",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Relationship = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_guardians", x => x.Id);
                    table.ForeignKey(
                        name: "FK_student_guardians_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "qmgr",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_student_guardians_visitor_profiles_VisitorProfileId",
                        column: x => x.VisitorProfileId,
                        principalSchema: "qmgr",
                        principalTable: "visitor_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "token_history",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    CounterId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_token_history_counters_CounterId",
                        column: x => x.CounterId,
                        principalSchema: "qmgr",
                        principalTable: "counters",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_token_history_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "qmgr",
                        principalTable: "tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_token_history_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoginTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LogoutTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TokensServed = table.Column<int>(type: "integer", nullable: false),
                    AverageServiceTimeSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_sessions_counters_CounterId",
                        column: x => x.CounterId,
                        principalSchema: "qmgr",
                        principalTable: "counters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_user_sessions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visitors",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    BadgeCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VehiclePlate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    HostUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StudentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckedOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BadgeConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConsentGivenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VisitorPassId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visitors_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_visitors_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_visitors_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "qmgr",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_visitors_users_HostUserId",
                        column: x => x.HostUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_visitors_visitor_passes_VisitorPassId",
                        column: x => x.VisitorPassId,
                        principalSchema: "qmgr",
                        principalTable: "visitor_passes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_visitors_visitor_profiles_VisitorProfileId",
                        column: x => x.VisitorProfileId,
                        principalSchema: "qmgr",
                        principalTable: "visitor_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ad_impressions_BranchId",
                schema: "qmgr",
                table: "ad_impressions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ad_impressions_DisplayId",
                schema: "qmgr",
                table: "ad_impressions",
                column: "DisplayId");

            migrationBuilder.CreateIndex(
                name: "IX_ad_impressions_OrganizationId_Date_AdSlot",
                schema: "qmgr",
                table: "ad_impressions",
                columns: new[] { "OrganizationId", "Date", "AdSlot" });

            migrationBuilder.CreateIndex(
                name: "idx_api_clients_client_id",
                schema: "qmgr",
                table: "api_clients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_OrganizationId",
                schema: "qmgr",
                table: "api_clients",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "idx_api_logs_client_date",
                schema: "qmgr",
                table: "api_logs",
                columns: new[] { "ApiClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_branch_settings_BranchId",
                schema: "qmgr",
                table: "branch_settings",
                column: "BranchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_branches_org_code",
                schema: "qmgr",
                table: "branches",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_broadcast_attachments_broadcast",
                schema: "qmgr",
                table: "broadcast_attachments",
                column: "BroadcastId");

            migrationBuilder.CreateIndex(
                name: "idx_broadcast_recipients_status",
                schema: "qmgr",
                table: "broadcast_recipients",
                columns: new[] { "BroadcastId", "Status" });

            migrationBuilder.CreateIndex(
                name: "idx_broadcast_recipients_unique",
                schema: "qmgr",
                table: "broadcast_recipients",
                columns: new[] { "BroadcastId", "ContactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_recipients_ContactId",
                schema: "qmgr",
                table: "broadcast_recipients",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "idx_broadcasts_org_status",
                schema: "qmgr",
                table: "broadcasts",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "idx_broadcasts_status_scheduled",
                schema: "qmgr",
                table: "broadcasts",
                columns: new[] { "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_broadcasts_BranchId",
                schema: "qmgr",
                table: "broadcasts",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "idx_campaign_impressions_campaign_time",
                schema: "qmgr",
                table: "campaign_impressions",
                columns: new[] { "CampaignId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_impressions_MediaContentId",
                schema: "qmgr",
                table: "campaign_impressions",
                column: "MediaContentId");

            migrationBuilder.CreateIndex(
                name: "idx_campaigns_branch_dates",
                schema: "qmgr",
                table: "campaigns",
                columns: new[] { "BranchId", "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "idx_contacts_optout_token",
                schema: "qmgr",
                table: "contacts",
                column: "OptOutToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_contacts_org_optedout",
                schema: "qmgr",
                table: "contacts",
                columns: new[] { "OrganizationId", "OptedOut" });

            migrationBuilder.CreateIndex(
                name: "IX_contacts_BranchId",
                schema: "qmgr",
                table: "contacts",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "idx_counter_service_types_unique",
                schema: "qmgr",
                table: "counter_service_types",
                columns: new[] { "CounterId", "ServiceTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_counter_service_types_ServiceTypeId",
                schema: "qmgr",
                table: "counter_service_types",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "idx_counters_branch_number",
                schema: "qmgr",
                table: "counters",
                columns: new[] { "BranchId", "CounterNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_counters_AssignedUserId",
                schema: "qmgr",
                table: "counters",
                column: "AssignedUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_counters_CurrentTokenId",
                schema: "qmgr",
                table: "counters",
                column: "CurrentTokenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_display_zones_DisplayId",
                schema: "qmgr",
                table: "display_zones",
                column: "DisplayId");

            migrationBuilder.CreateIndex(
                name: "IX_display_zones_PlaylistId",
                schema: "qmgr",
                table: "display_zones",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_displays_BranchId",
                schema: "qmgr",
                table: "displays",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_branch_date",
                schema: "qmgr",
                table: "feedbacks",
                columns: new[] { "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_feedback_branch_rating",
                schema: "qmgr",
                table: "feedbacks",
                columns: new[] { "BranchId", "Rating" });

            migrationBuilder.CreateIndex(
                name: "idx_feedback_code",
                schema: "qmgr",
                table: "feedbacks",
                column: "FeedbackCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_feedback_token",
                schema: "qmgr",
                table: "feedbacks",
                column: "TokenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_CounterId",
                schema: "qmgr",
                table: "feedbacks",
                column: "CounterId");

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_ServiceTypeId",
                schema: "qmgr",
                table: "feedbacks",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_InvoiceNumber",
                schema: "qmgr",
                table: "invoices",
                column: "InvoiceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_OrganizationId",
                schema: "qmgr",
                table: "invoices",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_SubscriptionId",
                schema: "qmgr",
                table: "invoices",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "idx_media_content_org_type",
                schema: "qmgr",
                table: "media_content",
                columns: new[] { "OrganizationId", "ContentType" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_NotificationId",
                schema: "qmgr",
                table: "NotificationLogs",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId",
                schema: "qmgr",
                table: "Notifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_OrganizationId",
                schema: "qmgr",
                table: "NotificationSettings",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "idx_organizations_custom_domain",
                schema: "qmgr",
                table: "organizations",
                column: "CustomDomain",
                unique: true,
                filter: "\"CustomDomain\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_organizations_name",
                schema: "qmgr",
                table: "organizations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_organizations_slug",
                schema: "qmgr",
                table: "organizations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_SubscriptionId",
                schema: "qmgr",
                table: "organizations",
                column: "SubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_InvoiceId",
                schema: "qmgr",
                table: "payments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_OrganizationId",
                schema: "qmgr",
                table: "payments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_ReferenceId",
                schema: "qmgr",
                table: "payments",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_SubscriptionId",
                schema: "qmgr",
                table: "payments",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "idx_permissions_code",
                schema: "qmgr",
                table: "permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_playlist_items_position",
                schema: "qmgr",
                table: "playlist_items",
                columns: new[] { "PlaylistId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_items_CampaignId",
                schema: "qmgr",
                table: "playlist_items",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_items_MediaContentId",
                schema: "qmgr",
                table: "playlist_items",
                column: "MediaContentId");

            migrationBuilder.CreateIndex(
                name: "IX_playlists_BranchId",
                schema: "qmgr",
                table: "playlists",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_quotes_OrganizationId",
                schema: "qmgr",
                table: "quotes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_PermissionId",
                schema: "qmgr",
                table: "role_permissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "idx_roles_org_code",
                schema: "qmgr",
                table: "roles",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_roster_import_job_entries_job_row",
                schema: "qmgr",
                table: "roster_import_job_entries",
                columns: new[] { "RosterImportJobId", "RowNumber" });

            migrationBuilder.CreateIndex(
                name: "idx_roster_import_jobs_branch_created",
                schema: "qmgr",
                table: "roster_import_jobs",
                columns: new[] { "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_roster_import_jobs_OrganizationId",
                schema: "qmgr",
                table: "roster_import_jobs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "idx_service_types_branch_code",
                schema: "qmgr",
                table: "service_types",
                columns: new[] { "BranchId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_student_guardians_profile",
                schema: "qmgr",
                table: "student_guardians",
                column: "VisitorProfileId");

            migrationBuilder.CreateIndex(
                name: "idx_student_guardians_unique_pair",
                schema: "qmgr",
                table: "student_guardians",
                columns: new[] { "StudentId", "VisitorProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_students_branch_active",
                schema: "qmgr",
                table: "students",
                columns: new[] { "BranchId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "idx_students_org_code_unique",
                schema: "qmgr",
                table: "students",
                columns: new[] { "OrganizationId", "StudentCode" },
                unique: true,
                filter: "\"StudentCode\" IS NOT NULL AND \"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plans_Code",
                schema: "qmgr",
                table: "subscription_plans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_OrganizationId",
                schema: "qmgr",
                table: "subscriptions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_PlanId",
                schema: "qmgr",
                table: "subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "idx_token_history_token",
                schema: "qmgr",
                table: "token_history",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_token_history_CounterId",
                schema: "qmgr",
                table: "token_history",
                column: "CounterId");

            migrationBuilder.CreateIndex(
                name: "IX_token_history_UserId",
                schema: "qmgr",
                table: "token_history",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "idx_tokens_branch_date",
                schema: "qmgr",
                table: "tokens",
                columns: new[] { "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_tokens_customer",
                schema: "qmgr",
                table: "tokens",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "idx_tokens_display_number",
                schema: "qmgr",
                table: "tokens",
                column: "DisplayNumber");

            migrationBuilder.CreateIndex(
                name: "idx_tokens_external",
                schema: "qmgr",
                table: "tokens",
                columns: new[] { "ExternalSystem", "ExternalReference" });

            migrationBuilder.CreateIndex(
                name: "idx_tokens_status",
                schema: "qmgr",
                table: "tokens",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_tokens_CounterId",
                schema: "qmgr",
                table: "tokens",
                column: "CounterId");

            migrationBuilder.CreateIndex(
                name: "IX_tokens_ServiceTypeId",
                schema: "qmgr",
                table: "tokens",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_usage_records_OrganizationId_Year_Month",
                schema: "qmgr",
                table: "usage_records",
                columns: new[] { "OrganizationId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_user_sessions_user_login",
                schema: "qmgr",
                table: "user_sessions",
                columns: new[] { "UserId", "LoginTime" });

            migrationBuilder.CreateIndex(
                name: "IX_user_sessions_CounterId",
                schema: "qmgr",
                table: "user_sessions",
                column: "CounterId");

            migrationBuilder.CreateIndex(
                name: "idx_users_email",
                schema: "qmgr",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_users_username",
                schema: "qmgr",
                table: "users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_AssignedBranchId",
                schema: "qmgr",
                table: "users",
                column: "AssignedBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_users_OrganizationId",
                schema: "qmgr",
                table: "users",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_users_RoleId",
                schema: "qmgr",
                table: "users",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "idx_visitor_passes_branch_active",
                schema: "qmgr",
                table: "visitor_passes",
                columns: new[] { "BranchId", "RevokedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "idx_visitor_passes_token",
                schema: "qmgr",
                table: "visitor_passes",
                column: "TokenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_visitor_passes_OrganizationId",
                schema: "qmgr",
                table: "visitor_passes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "idx_visitor_profiles_name_prefix",
                schema: "qmgr",
                table: "visitor_profiles",
                column: "FullName")
                .Annotation("Npgsql:IndexMethod", "btree")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "idx_visitor_profiles_org_deleted",
                schema: "qmgr",
                table: "visitor_profiles",
                columns: new[] { "OrganizationId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_visitor_profiles_org_email_unique",
                schema: "qmgr",
                table: "visitor_profiles",
                columns: new[] { "OrganizationId", "NormalizedEmail" },
                unique: true,
                filter: "\"NormalizedEmail\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_visitor_profiles_org_id_unique",
                schema: "qmgr",
                table: "visitor_profiles",
                columns: new[] { "OrganizationId", "NormalizedIdNumber" },
                unique: true,
                filter: "\"NormalizedIdNumber\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_visitor_profiles_org_phone_unique",
                schema: "qmgr",
                table: "visitor_profiles",
                columns: new[] { "OrganizationId", "NormalizedPhone" },
                unique: true,
                filter: "\"NormalizedPhone\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_badge",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "BadgeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_checkedin",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "CheckedInAt" });

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_deleted",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_visitors_branch_status",
                schema: "qmgr",
                table: "visitors",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "idx_visitors_profile_active_unique",
                schema: "qmgr",
                table: "visitors",
                column: "VisitorProfileId",
                unique: true,
                filter: "\"Status\" = 'CheckedIn' AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_visitors_HostUserId",
                schema: "qmgr",
                table: "visitors",
                column: "HostUserId");

            migrationBuilder.CreateIndex(
                name: "IX_visitors_OrganizationId",
                schema: "qmgr",
                table: "visitors",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_visitors_StudentId",
                schema: "qmgr",
                table: "visitors",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_visitors_VisitorPassId",
                schema: "qmgr",
                table: "visitors",
                column: "VisitorPassId");

            migrationBuilder.CreateIndex(
                name: "idx_webhooks_status_date",
                schema: "qmgr",
                table: "webhooks_outgoing",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhooks_outgoing_ApiClientId",
                schema: "qmgr",
                table: "webhooks_outgoing",
                column: "ApiClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_ad_impressions_branches_BranchId",
                schema: "qmgr",
                table: "ad_impressions",
                column: "BranchId",
                principalSchema: "qmgr",
                principalTable: "branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ad_impressions_displays_DisplayId",
                schema: "qmgr",
                table: "ad_impressions",
                column: "DisplayId",
                principalSchema: "qmgr",
                principalTable: "displays",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ad_impressions_organizations_OrganizationId",
                schema: "qmgr",
                table: "ad_impressions",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_api_clients_organizations_OrganizationId",
                schema: "qmgr",
                table: "api_clients",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_branch_settings_branches_BranchId",
                schema: "qmgr",
                table: "branch_settings",
                column: "BranchId",
                principalSchema: "qmgr",
                principalTable: "branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_branches_organizations_OrganizationId",
                schema: "qmgr",
                table: "branches",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_broadcast_attachments_broadcasts_BroadcastId",
                schema: "qmgr",
                table: "broadcast_attachments",
                column: "BroadcastId",
                principalSchema: "qmgr",
                principalTable: "broadcasts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_broadcast_recipients_broadcasts_BroadcastId",
                schema: "qmgr",
                table: "broadcast_recipients",
                column: "BroadcastId",
                principalSchema: "qmgr",
                principalTable: "broadcasts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_broadcast_recipients_contacts_ContactId",
                schema: "qmgr",
                table: "broadcast_recipients",
                column: "ContactId",
                principalSchema: "qmgr",
                principalTable: "contacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_broadcasts_organizations_OrganizationId",
                schema: "qmgr",
                table: "broadcasts",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_campaign_impressions_media_content_MediaContentId",
                schema: "qmgr",
                table: "campaign_impressions",
                column: "MediaContentId",
                principalSchema: "qmgr",
                principalTable: "media_content",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_contacts_organizations_OrganizationId",
                schema: "qmgr",
                table: "contacts",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_counter_service_types_counters_CounterId",
                schema: "qmgr",
                table: "counter_service_types",
                column: "CounterId",
                principalSchema: "qmgr",
                principalTable: "counters",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_counters_tokens_CurrentTokenId",
                schema: "qmgr",
                table: "counters",
                column: "CurrentTokenId",
                principalSchema: "qmgr",
                principalTable: "tokens",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_counters_users_AssignedUserId",
                schema: "qmgr",
                table: "counters",
                column: "AssignedUserId",
                principalSchema: "qmgr",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_organizations_OrganizationId",
                schema: "qmgr",
                table: "invoices",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_subscriptions_SubscriptionId",
                schema: "qmgr",
                table: "invoices",
                column: "SubscriptionId",
                principalSchema: "qmgr",
                principalTable: "subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_media_content_organizations_OrganizationId",
                schema: "qmgr",
                table: "media_content",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_Notifications_NotificationId",
                schema: "qmgr",
                table: "NotificationLogs",
                column: "NotificationId",
                principalSchema: "qmgr",
                principalTable: "Notifications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_users_UserId",
                schema: "qmgr",
                table: "Notifications",
                column: "UserId",
                principalSchema: "qmgr",
                principalTable: "users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationSettings_organizations_OrganizationId",
                schema: "qmgr",
                table: "NotificationSettings",
                column: "OrganizationId",
                principalSchema: "qmgr",
                principalTable: "organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_organizations_subscriptions_SubscriptionId",
                schema: "qmgr",
                table: "organizations",
                column: "SubscriptionId",
                principalSchema: "qmgr",
                principalTable: "subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_counters_branches_BranchId",
                schema: "qmgr",
                table: "counters");

            migrationBuilder.DropForeignKey(
                name: "FK_service_types_branches_BranchId",
                schema: "qmgr",
                table: "service_types");

            migrationBuilder.DropForeignKey(
                name: "FK_tokens_branches_BranchId",
                schema: "qmgr",
                table: "tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_users_branches_AssignedBranchId",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropForeignKey(
                name: "FK_roles_organizations_OrganizationId",
                schema: "qmgr",
                table: "roles");

            migrationBuilder.DropForeignKey(
                name: "FK_subscriptions_organizations_OrganizationId",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_users_organizations_OrganizationId",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropForeignKey(
                name: "FK_tokens_counters_CounterId",
                schema: "qmgr",
                table: "tokens");

            migrationBuilder.DropTable(
                name: "ad_impressions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "api_logs",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "branch_settings",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "broadcast_attachments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "broadcast_recipients",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "campaign_impressions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "counter_service_types",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "display_zones",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "feedbacks",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "NotificationLogs",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "NotificationSettings",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "platform_spotify_connections",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "PlatformConfigurations",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "PlatformSettings",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "playlist_items",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "roster_import_job_entries",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "student_guardians",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "token_history",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "usage_records",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "visitors",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "webhooks_outgoing",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "broadcasts",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "contacts",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "displays",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "media_content",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "playlists",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "roster_import_jobs",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "students",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "visitor_passes",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "visitor_profiles",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "api_clients",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "branches",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "subscription_plans",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "counters",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "tokens",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "users",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "service_types",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "qmgr");
        }
    }
}
