using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffPerformanceGroundwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "DepartmentIds",
                schema: "qmgr",
                table: "users",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LineManagerUserId",
                schema: "qmgr",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StaffScope",
                schema: "qmgr",
                table: "roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EventKey",
                schema: "qmgr",
                table: "Notifications",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActivityEvents",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DetailJson = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HeadUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeputyHeadUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Departments_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Departments_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Departments_users_DeputyHeadUserId",
                        column: x => x.DeputyHeadUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Departments_users_HeadUserId",
                        column: x => x.HeadUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PerformanceParameters",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AppliesTo = table.Column<int>(type: "integer", nullable: false),
                    DefaultPoints = table.Column<int>(type: "integer", nullable: true),
                    MaxPointsPerEntry = table.Column<int>(type: "integer", nullable: false),
                    MaxPointsPerPeriod = table.Column<int>(type: "integer", nullable: true),
                    Weight = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    RatingScale = table.Column<int>(type: "integer", nullable: true),
                    RubricJson = table.Column<string>(type: "text", nullable: true),
                    DefaultVisibility = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsSystemSource = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerformanceParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PerformanceParameters_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffAppraisals",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    AppraiserUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    TargetsJson = table.Column<string>(type: "text", nullable: true),
                    ComputedScore = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    ComputedBreakdownJson = table.Column<string>(type: "text", nullable: true),
                    SelfRatingsJson = table.Column<string>(type: "text", nullable: true),
                    SelfRating = table.Column<int>(type: "integer", nullable: true),
                    SelfComments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AppraiserRating = table.Column<int>(type: "integer", nullable: true),
                    AppraiserComments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    FinalRating = table.Column<int>(type: "integer", nullable: true),
                    Strengths = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DevelopmentAreas = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SupportPlanJson = table.Column<string>(type: "text", nullable: true),
                    NextTargetsJson = table.Column<string>(type: "text", nullable: true),
                    SelfSubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppraiserSubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModeratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ModerationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppealNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReportMediaContentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffAppraisals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffAppraisals_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffAppraisals_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffAppraisals_users_AppraiserUserId",
                        column: x => x.AppraiserUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffAppraisals_users_SubjectUserId",
                        column: x => x.SubjectUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffNotices",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    AudienceDepartmentIds = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    AudienceRoleCodes = table.Column<string[]>(type: "text[]", nullable: true),
                    AudienceStaffGroup = table.Column<int>(type: "integer", nullable: true),
                    PublishAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresAcknowledgement = table.Column<bool>(type: "boolean", nullable: false),
                    AttachmentMediaContentIds = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    Acknowledgements = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationsSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffNotices_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffNotices_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffDuties",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParameterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedUserIds = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    RecorderUserIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    RegisterOpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RegisterClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RegisterClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MinutesMediaContentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RegisterChaseSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffDuties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffDuties_PerformanceParameters_ParameterId",
                        column: x => x.ParameterId,
                        principalSchema: "qmgr",
                        principalTable: "PerformanceParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffDuties_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffDuties_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffPerformanceRecords",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParameterId = table.Column<Guid>(type: "uuid", nullable: false),
                    DutyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    LoggedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPerformanceRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceRecords_PerformanceParameters_ParameterId",
                        column: x => x.ParameterId,
                        principalSchema: "qmgr",
                        principalTable: "PerformanceParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceRecords_StaffDuties_DutyId",
                        column: x => x.DutyId,
                        principalSchema: "qmgr",
                        principalTable: "StaffDuties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceRecords_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceRecords_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceRecords_users_SubjectUserId",
                        column: x => x.SubjectUserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StaffPerformanceAttachments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPerformanceAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceAttachments_StaffPerformanceRecords_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "qmgr",
                        principalTable: "StaffPerformanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StaffPerformanceNotes",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffPerformanceNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffPerformanceNotes_StaffPerformanceRecords_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "qmgr",
                        principalTable: "StaffPerformanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_activity_actor_occurred",
                schema: "qmgr",
                table: "ActivityEvents",
                columns: new[] { "ActorUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "idx_activity_attribution_pending",
                schema: "qmgr",
                table: "ActivityEvents",
                column: "OccurredAt",
                filter: "\"IpAddress\" IS NOT NULL OR \"UserAgent\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_activity_org_occurred",
                schema: "qmgr",
                table: "ActivityEvents",
                columns: new[] { "OrganizationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "idx_activity_subject_occurred",
                schema: "qmgr",
                table: "ActivityEvents",
                columns: new[] { "SubjectUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "idx_departments_head",
                schema: "qmgr",
                table: "Departments",
                column: "HeadUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_BranchId",
                schema: "qmgr",
                table: "Departments",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_DeputyHeadUserId",
                schema: "qmgr",
                table: "Departments",
                column: "DeputyHeadUserId");

            migrationBuilder.CreateIndex(
                name: "ux_departments_org_code_active",
                schema: "qmgr",
                table: "Departments",
                columns: new[] { "OrganizationId", "Code" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.CreateIndex(
                name: "idx_performance_parameters_org_sort",
                schema: "qmgr",
                table: "PerformanceParameters",
                columns: new[] { "OrganizationId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "idx_staff_appraisals_appraiser",
                schema: "qmgr",
                table: "StaffAppraisals",
                column: "AppraiserUserId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_appraisals_branch_period_stage",
                schema: "qmgr",
                table: "StaffAppraisals",
                columns: new[] { "BranchId", "PeriodKey", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffAppraisals_OrganizationId",
                schema: "qmgr",
                table: "StaffAppraisals",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "ux_staff_appraisals_subject_period",
                schema: "qmgr",
                table: "StaffAppraisals",
                columns: new[] { "SubjectUserId", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_branch_start",
                schema: "qmgr",
                table: "StaffDuties",
                columns: new[] { "BranchId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "idx_staff_duties_register_open",
                schema: "qmgr",
                table: "StaffDuties",
                column: "EndsAt",
                filter: "\"RegisterClosedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDuties_OrganizationId",
                schema: "qmgr",
                table: "StaffDuties",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDuties_ParameterId",
                schema: "qmgr",
                table: "StaffDuties",
                column: "ParameterId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_notices_org_publish",
                schema: "qmgr",
                table: "StaffNotices",
                columns: new[] { "OrganizationId", "PublishAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffNotices_BranchId",
                schema: "qmgr",
                table: "StaffNotices",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_attachments_file_url",
                schema: "qmgr",
                table: "StaffPerformanceAttachments",
                column: "FileUrl");

            migrationBuilder.CreateIndex(
                name: "idx_staff_attachments_record",
                schema: "qmgr",
                table: "StaffPerformanceAttachments",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_notes_record",
                schema: "qmgr",
                table: "StaffPerformanceNotes",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_records_branch_occurred",
                schema: "qmgr",
                table: "StaffPerformanceRecords",
                columns: new[] { "BranchId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "idx_staff_records_duty",
                schema: "qmgr",
                table: "StaffPerformanceRecords",
                column: "DutyId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_records_logged_by",
                schema: "qmgr",
                table: "StaffPerformanceRecords",
                column: "LoggedByUserId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_records_parameter",
                schema: "qmgr",
                table: "StaffPerformanceRecords",
                column: "ParameterId");

            migrationBuilder.CreateIndex(
                name: "idx_staff_records_subject_occurred",
                schema: "qmgr",
                table: "StaffPerformanceRecords",
                columns: new[] { "SubjectUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffPerformanceRecords_OrganizationId",
                schema: "qmgr",
                table: "StaffPerformanceRecords",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEvents",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "Departments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffAppraisals",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffNotices",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffPerformanceAttachments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffPerformanceNotes",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffPerformanceRecords",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "StaffDuties",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "PerformanceParameters",
                schema: "qmgr");

            migrationBuilder.DropColumn(
                name: "DepartmentIds",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LineManagerUserId",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "StaffScope",
                schema: "qmgr",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "EventKey",
                schema: "qmgr",
                table: "Notifications");
        }
    }
}
