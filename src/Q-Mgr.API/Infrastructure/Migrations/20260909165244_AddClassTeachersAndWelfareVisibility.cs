using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClassTeachersAndWelfareVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Confidential (bool) → Visibility (enum), WITHOUT losing the existing data ───────
            //
            // EF scaffolded this as DropColumn-then-AddColumn, which would have silently
            // reclassified every existing safeguarding record as Standard — readable by everyone
            // with welfare.view, on the next request, with nothing anywhere to say it had happened.
            // Add, backfill, THEN drop. The order is the whole point.
            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Schema-qualified explicitly. Raw SQL does NOT inherit HasDefaultSchema("qmgr") — it
            // resolves through the connection's search_path, which for this database is
            // ("$user", public) and holds none of this app's tables. An unqualified name here
            // would hard-fail the migration. (Project rule, learned the hard way in Phase 57.)
            migrationBuilder.Sql(@"
                UPDATE qmgr.""WelfareRecords""
                SET ""Visibility"" = 1
                WHERE ""Confidential"" = TRUE;");

            migrationBuilder.DropColumn(
                name: "Confidential",
                schema: "qmgr",
                table: "WelfareRecords");

            migrationBuilder.AddColumn<string>(
                name: "AlternatePhone",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobTitle",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotificationPreferences",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfficeLocation",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestrictedNotes",
                schema: "qmgr",
                table: "students",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RestrictedNotesUpdatedAt",
                schema: "qmgr",
                table: "students",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RestrictedNotesUpdatedByUserId",
                schema: "qmgr",
                table: "students",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                schema: "qmgr",
                table: "StudentFlags",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "StaffNotifyEmail",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "StaffNotifySms",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ClassTeacherAssignments",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassTeacherAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassTeacherAssignments_branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "qmgr",
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassTeacherAssignments_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "qmgr",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassTeacherAssignments_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_class_teacher_branch_class_live",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                columns: new[] { "BranchId", "ClassName" },
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_class_teacher_user_live",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                column: "UserId",
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassTeacherAssignments_OrganizationId",
                schema: "qmgr",
                table: "ClassTeacherAssignments",
                column: "OrganizationId");

            // At most one LIVE class teacher (Role = 0) per class.
            //
            // Hand-written rather than left as the EF-scaffolded index because it has to be on
            // lower("ClassName"): class names are user-typed on both sides, and an index on the raw
            // column would happily accept "S4B" and "s4b" as two different classes each with their
            // own primary teacher — which is exactly the split the whole normalization rule exists
            // to prevent. EF Core cannot express an expression index declaratively, so this is raw
            // SQL, schema-qualified.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_class_teacher_one_primary_per_class
                ON qmgr.""ClassTeacherAssignments"" (""BranchId"", LOWER(TRIM(""ClassName"")))
                WHERE ""EndedAt"" IS NULL AND ""Role"" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassTeacherAssignments",
                schema: "qmgr");

            // WelfareRecords.Visibility is dropped at the END of this method instead, after its
            // value has been read back into the restored Confidential bool.

            migrationBuilder.DropColumn(
                name: "AlternatePhone",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "JobTitle",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "NotificationPreferences",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "OfficeLocation",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "RestrictedNotes",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "RestrictedNotesUpdatedAt",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "RestrictedNotesUpdatedByUserId",
                schema: "qmgr",
                table: "students");

            migrationBuilder.DropColumn(
                name: "Visibility",
                schema: "qmgr",
                table: "StudentFlags");

            migrationBuilder.DropColumn(
                name: "StaffNotifyEmail",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "StaffNotifySms",
                schema: "qmgr",
                table: "NotificationSettings");

            // The reverse of the Up backfill, and in the mirrored order: add, backfill, then drop.
            // A rollback that silently un-confidentials every safeguarding record is the same bug
            // as the forward one, and rollbacks are run in exactly the circumstances where nobody
            // is watching closely.
            //
            // Note the lossy part, honestly: Restricted (2) collapses back to Confidential=true,
            // because the bool cannot represent three levels. Rolling forward again would leave
            // those records at Confidential rather than Restricted. That is unavoidable in a
            // three-into-two conversion and is why the Up is the one to get right.
            migrationBuilder.AddColumn<bool>(
                name: "Confidential",
                schema: "qmgr",
                table: "WelfareRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(@"
                UPDATE qmgr.""WelfareRecords""
                SET ""Confidential"" = TRUE
                WHERE ""Visibility"" >= 1;");

            migrationBuilder.DropColumn(
                name: "Visibility",
                schema: "qmgr",
                table: "WelfareRecords");
        }
    }
}
