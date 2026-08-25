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
                name: "PasswordPolicies",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MinimumLength = table.Column<int>(type: "integer", nullable: false),
                    MaximumLength = table.Column<int>(type: "integer", nullable: false),
                    RequireUppercase = table.Column<bool>(type: "boolean", nullable: false),
                    RequireLowercase = table.Column<bool>(type: "boolean", nullable: false),
                    RequireDigits = table.Column<bool>(type: "boolean", nullable: false),
                    RequireSpecialCharacters = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedSpecialCharacters = table.Column<string>(type: "text", nullable: false),
                    PreventCommonPasswords = table.Column<bool>(type: "boolean", nullable: false),
                    PreventUserInfoInPassword = table.Column<bool>(type: "boolean", nullable: false),
                    MinimumUniqueCharacters = table.Column<int>(type: "integer", nullable: false),
                    EnablePasswordHistory = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHistoryCount = table.Column<int>(type: "integer", nullable: false),
                    EnablePasswordExpiry = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordExpiryDays = table.Column<int>(type: "integer", nullable: false),
                    EnableAccountLockout = table.Column<bool>(type: "boolean", nullable: false),
                    MaxFailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockoutDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordPolicies", x => x.Id);
                });

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
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_items", x => x.Id);
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
                name: "idx_branches_code",
                schema: "qmgr",
                table: "branches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_OrganizationId",
                schema: "qmgr",
                table: "branches",
                column: "OrganizationId");

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
                column: "TokenId");

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
                name: "idx_service_types_branch_code",
                schema: "qmgr",
                table: "service_types",
                columns: new[] { "BranchId", "Code" },
                unique: true);

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
                name: "PasswordPolicies",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "payments",
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
                name: "token_history",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "usage_records",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "webhooks_outgoing",
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
                name: "media_content",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "playlists",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "permissions",
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
