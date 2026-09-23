using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// EVERY NOTIFICATION NAMES ONE PERSON (2026-09-23). A notification with no recipient was read by
    /// the whole school and pushed live to every tenant on the platform; a newly onboarded teacher at
    /// Maryhill read the school's failed payments in their bell.
    ///
    /// <para>The rows that already exist with no recipient are DELETED rather than reassigned (user
    /// decision D1): nobody can be given them correctly — they were written for "whoever", and handing
    /// a payment amount to a guessed set of people is the leak this migration closes. Their delivery
    /// log rows go with them (the foreign key cascades). The column then becomes NOT NULL, so the rule
    /// holds even for code that writes a row without going through NotificationService.</para>
    ///
    /// <para>The scaffold offered a default of the all-zeros GUID for the NOT NULL change. It is
    /// removed: every surviving row already has a recipient, and a zero id would satisfy the NOT
    /// NULL while failing the foreign key.</para>
    ///
    /// <para><c>TrialReminderSentAt</c> is the claim the daily trial-reminder job takes before it tells
    /// anybody, so a retried run cannot send the same reminder twice in a day.</para>
    /// </summary>
    public partial class NotificationsNameOneRecipient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE FROM qmgr.""Notifications"" WHERE ""UserId"" IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_users_UserId",
                schema: "qmgr",
                table: "Notifications");

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialReminderSentAt",
                schema: "qmgr",
                table: "organization_modules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "qmgr",
                table: "Notifications",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_users_UserId",
                schema: "qmgr",
                table: "Notifications",
                column: "UserId",
                principalSchema: "qmgr",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        /// <remarks>Restores the nullable column and the old foreign key. The deleted rows are not
        /// recoverable, and inventing them back would be worse than their absence.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_users_UserId",
                schema: "qmgr",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "TrialReminderSentAt",
                schema: "qmgr",
                table: "organization_modules");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "qmgr",
                table: "Notifications",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_users_UserId",
                schema: "qmgr",
                table: "Notifications",
                column: "UserId",
                principalSchema: "qmgr",
                principalTable: "users",
                principalColumn: "Id");
        }
    }
}
