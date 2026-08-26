using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorProfilesAndPasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VisitorPassId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

            // Nullable for now — populated by the data backfill below, then locked to NOT NULL
            // once every existing row has a profile.
            migrationBuilder.AddColumn<Guid>(
                name: "VisitorProfileId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: true);

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

            // Data backfill: one profile per existing visit row (1:1 — this codebase's existing
            // visitor rows predate returning-visitor recognition, so there's no dedup to do
            // here; a real production cutover with genuinely duplicate contact data across rows
            // would need a merge pass first). Reuses the visit's own Id as the new profile's Id
            // so the two are trivially correlated without a temporary join column.
            migrationBuilder.Sql(@"
                INSERT INTO qmgr.visitor_profiles (
                    ""Id"", ""OrganizationId"", ""FullName"", ""Phone"", ""Email"", ""Company"", ""IdNumber"", ""PhotoUrl"",
                    ""NormalizedPhone"", ""NormalizedEmail"", ""NormalizedIdNumber"",
                    ""IsWatchlisted"", ""WatchlistReason"", ""CreatedAt"", ""UpdatedAt"", ""IsActive""
                )
                SELECT
                    v.""Id"", v.""OrganizationId"", v.""FullName"", v.""Phone"", v.""Email"", v.""Company"", v.""IdNumber"", v.""PhotoUrl"",
                    NULLIF(regexp_replace(v.""Phone"", '[^0-9+]', '', 'g'), ''),
                    NULLIF(lower(trim(v.""Email"")), ''),
                    NULLIF(upper(trim(v.""IdNumber"")), ''),
                    v.""IsWatchlisted"", v.""WatchlistReason"", v.""CreatedAt"", v.""UpdatedAt"", true
                FROM qmgr.visitors v;

                UPDATE qmgr.visitors SET ""VisitorProfileId"" = ""Id"";
            ");

            migrationBuilder.AlterColumn<Guid>(
                name: "VisitorProfileId",
                schema: "qmgr",
                table: "visitors",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Company",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "Email",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "FullName",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "IdNumber",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "IsWatchlisted",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "Phone",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "WatchlistReason",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.CreateIndex(
                name: "idx_visitors_profile_active_unique",
                schema: "qmgr",
                table: "visitors",
                column: "VisitorProfileId",
                unique: true,
                filter: "\"Status\" = 'CheckedIn' AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_visitors_VisitorPassId",
                schema: "qmgr",
                table: "visitors",
                column: "VisitorPassId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_visitors_visitor_passes_VisitorPassId",
                schema: "qmgr",
                table: "visitors",
                column: "VisitorPassId",
                principalSchema: "qmgr",
                principalTable: "visitor_passes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_visitors_visitor_profiles_VisitorProfileId",
                schema: "qmgr",
                table: "visitors",
                column: "VisitorProfileId",
                principalSchema: "qmgr",
                principalTable: "visitor_profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_visitors_visitor_passes_VisitorPassId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropForeignKey(
                name: "FK_visitors_visitor_profiles_VisitorProfileId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropTable(
                name: "visitor_passes",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_visitors_profile_active_unique",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropIndex(
                name: "IX_visitors_VisitorPassId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.AddColumn<string>(
                name: "Company",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullName",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IdNumber",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsWatchlisted",
                schema: "qmgr",
                table: "visitors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                schema: "qmgr",
                table: "visitors",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchlistReason",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // Backfill the old flattened columns from the profile each visit still points to,
            // before the profile table (and the FK to it) goes away below.
            migrationBuilder.Sql(@"
                UPDATE qmgr.visitors v
                SET ""FullName"" = p.""FullName"", ""Phone"" = p.""Phone"", ""Email"" = p.""Email"",
                    ""Company"" = p.""Company"", ""IdNumber"" = p.""IdNumber"", ""PhotoUrl"" = p.""PhotoUrl"",
                    ""IsWatchlisted"" = p.""IsWatchlisted"", ""WatchlistReason"" = p.""WatchlistReason""
                FROM qmgr.visitor_profiles p
                WHERE p.""Id"" = v.""VisitorProfileId"";
            ");

            migrationBuilder.DropTable(
                name: "visitor_profiles",
                schema: "qmgr");

            migrationBuilder.DropColumn(
                name: "VehiclePlate",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "VisitorPassId",
                schema: "qmgr",
                table: "visitors");

            migrationBuilder.DropColumn(
                name: "VisitorProfileId",
                schema: "qmgr",
                table: "visitors");
        }
    }
}
