using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramWhatsAppChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TelegramBotToken",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TelegramEnabled",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppAccessToken",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppEnabled",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppPhoneNumberId",
                schema: "qmgr",
                table: "NotificationSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelegramChatId",
                schema: "qmgr",
                table: "contacts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelegramBotToken",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TelegramEnabled",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppAccessToken",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppEnabled",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppPhoneNumberId",
                schema: "qmgr",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TelegramChatId",
                schema: "qmgr",
                table: "contacts");
        }
    }
}
