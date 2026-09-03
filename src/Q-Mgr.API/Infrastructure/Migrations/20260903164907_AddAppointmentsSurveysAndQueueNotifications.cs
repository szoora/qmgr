using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentsSurveysAndQueueNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpectedArrivalAt",
                schema: "qmgr",
                table: "visitors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreRegisteredByUserId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisitorType",
                schema: "qmgr",
                table: "visitors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "InductionCompletedAt",
                schema: "qmgr",
                table: "visitor_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InductionNotes",
                schema: "qmgr",
                table: "visitor_profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WatchlistAddedAt",
                schema: "qmgr",
                table: "visitor_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WatchlistAddedByUserId",
                schema: "qmgr",
                table: "visitor_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotifiedAt",
                schema: "qmgr",
                table: "tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastNotifiedStage",
                schema: "qmgr",
                table: "tokens",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailReminderSubject",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailReminderTemplate",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QueueNotificationsEnabled",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "QueueNotifyEmail",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "QueueNotifyOnApproaching",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "QueueNotifyOnCalled",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "QueueNotifyOnIssued",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "QueueNotifySms",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NpsScore",
                schema: "qmgr",
                table: "feedbacks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsesJson",
                schema: "qmgr",
                table: "feedbacks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "appointments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenceCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CustomerPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CustomerEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExternalReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExternalSystem = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckedInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_appointments_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_service_types_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalSchema: "qmgr",
                        principalTable: "service_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "qmgr",
                        principalTable: "tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "feedback_questions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    QuestionText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    QuestionType = table.Column<int>(type: "integer", nullable: false),
                    OptionsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ServiceTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedback_questions_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feedback_questions_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feedback_questions_service_types_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalSchema: "qmgr",
                        principalTable: "service_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_appointments_branch_scheduled",
                schema: "qmgr",
                table: "appointments",
                columns: new[] { "BranchId", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "idx_appointments_branch_status",
                schema: "qmgr",
                table: "appointments",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "idx_appointments_external",
                schema: "qmgr",
                table: "appointments",
                columns: new[] { "ExternalSystem", "ExternalReference" });

            migrationBuilder.CreateIndex(
                name: "idx_appointments_reference_unique",
                schema: "qmgr",
                table: "appointments",
                columns: new[] { "BranchId", "ReferenceCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_appointments_status_scheduled",
                schema: "qmgr",
                table: "appointments",
                columns: new[] { "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_ServiceTypeId",
                schema: "qmgr",
                table: "appointments",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_TokenId",
                schema: "qmgr",
                table: "appointments",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_question_branch_order",
                schema: "qmgr",
                table: "feedback_questions",
                columns: new[] { "BranchId", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "idx_feedback_question_org",
                schema: "qmgr",
                table: "feedback_questions",
                columns: new[] { "OrganizationId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_questions_ServiceTypeId",
                schema: "qmgr",
                table: "feedback_questions",
                column: "ServiceTypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointments",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "feedback_questions",
                schema: "qmgr");

            migrationBuilder.DropColumn(
                name: "ExpectedArrivalAt",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "PreRegisteredByUserId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "VisitorType",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "InductionCompletedAt",
                schema: "qmgr",
                table: "visitor_profiles");

            migrationBuilder.DropColumn(
                name: "InductionNotes",
                schema: "qmgr",
                table: "visitor_profiles");

            migrationBuilder.DropColumn(
                name: "WatchlistAddedAt",
                schema: "qmgr",
                table: "visitor_profiles");

            migrationBuilder.DropColumn(
                name: "WatchlistAddedByUserId",
                schema: "qmgr",
                table: "visitor_profiles");

            migrationBuilder.DropColumn(
                name: "LastNotifiedAt",
                schema: "qmgr",
                table: "tokens");

            migrationBuilder.DropColumn(
                name: "LastNotifiedStage",
                schema: "qmgr",
                table: "tokens");

            migrationBuilder.DropColumn(
                name: "EmailReminderSubject",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "EmailReminderTemplate",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "QueueNotificationsEnabled",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "QueueNotifyEmail",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "QueueNotifyOnApproaching",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "QueueNotifyOnCalled",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "QueueNotifyOnIssued",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "QueueNotifySms",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "NpsScore",
                schema: "qmgr",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "ResponsesJson",
                schema: "qmgr",
                table: "feedbacks");
        }
    }
}
