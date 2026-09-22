using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// The mobile app's per-device sessions, and the credential version that kills them all at once.
    ///
    /// <para><b>Entirely ADDITIVE: one nullable column, one new table, three indexes.</b> Nothing is
    /// dropped, nothing is converted and no existing row changes, so it cannot reclassify or lose
    /// anything — which is worth stating because the last security-field migration in this project
    /// (<c>Confidential → Visibility</c>) was scaffolded as drop-then-add and would have silently
    /// reclassified every safeguarding record as readable.</para>
    ///
    /// <para><b><c>Users.CredentialStamp</c> is deliberately left NULL for every existing row</b>
    /// rather than backfilled with a value. <c>CredentialStamps.Matches</c> treats null on both sides
    /// as a match, so nobody is signed out by this migration — and there is nothing to sign out yet,
    /// because no device session can exist before the table does.</para>
    ///
    /// <para><b><c>User.RefreshToken</c> is untouched.</b> It remains the browser's single refresh
    /// token; the new table is the handsets'. Moving the web onto it is a separate decision with a
    /// blast radius across every signed-in tab.</para>
    /// </summary>
    public partial class AddMobileDeviceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CredentialStamp",
                schema: "qmgr",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserDeviceSessions",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AppVersionCode = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CredentialStamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PushToken = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PushPermitted = table.Column<bool>(type: "boolean", nullable: false),
                    PushTokenUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PushFailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckInAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDeviceSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserDeviceSessions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "qmgr",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_device_session_org",
                schema: "qmgr",
                table: "UserDeviceSessions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "idx_device_session_user_device",
                schema: "qmgr",
                table: "UserDeviceSessions",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_device_session_user_live",
                schema: "qmgr",
                table: "UserDeviceSessions",
                column: "UserId",
                filter: "\"RevokedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserDeviceSessions",
                schema: "qmgr");

            migrationBuilder.DropColumn(
                name: "CredentialStamp",
                schema: "qmgr",
                table: "users");
        }
    }
}
