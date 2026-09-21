using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLifecycleAndPurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_lifecycle_events",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NextTransitionDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_lifecycle_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_purge_certificates",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurgedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RowsDeletedJson = table.Column<string>(type: "jsonb", nullable: false),
                    FilesDeleted = table.Column<int>(type: "integer", nullable: false),
                    FilesFailed = table.Column<int>(type: "integer", nullable: false),
                    BackgroundJobsRemoved = table.Column<int>(type: "integer", nullable: false),
                    CacheKeysDropped = table.Column<int>(type: "integer", nullable: false),
                    RowsRetained = table.Column<int>(type: "integer", nullable: false),
                    VerificationPassed = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationDetail = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_purge_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_tombstones",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PurgedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PurgedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EmailDomainHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NameKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HadFinancialRecords = table.Column<bool>(type: "boolean", nullable: false),
                    StatutoryRetentionUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_tombstones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_lifecycle_events_OrganizationId_OccurredAt",
                schema: "qmgr",
                table: "tenant_lifecycle_events",
                columns: new[] { "OrganizationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_purge_certificates_OrganizationId_PurgedAt",
                schema: "qmgr",
                table: "tenant_purge_certificates",
                columns: new[] { "OrganizationId", "PurgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_tombstones_EmailDomainHash",
                schema: "qmgr",
                table: "tenant_tombstones",
                column: "EmailDomainHash");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_tombstones_NameKeyHash",
                schema: "qmgr",
                table: "tenant_tombstones",
                column: "NameKeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_tombstones_OrganizationId",
                schema: "qmgr",
                table: "tenant_tombstones",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_lifecycle_events",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "tenant_purge_certificates",
                schema: "qmgr");

            migrationBuilder.DropTable(
                name: "tenant_tombstones",
                schema: "qmgr");
        }
    }
}
