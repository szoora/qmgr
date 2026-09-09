using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the roles.DataScope column.
    ///
    /// NOT part of the visitor-reporting work — the Role entity already carried this property in
    /// the working tree with no migration behind it, so the column simply did not exist in any
    /// database. It surfaced because adding a visitor migration made EF start validating the model
    /// against the snapshot on every startup, at which point the API refused to boot. A fresh
    /// install would have failed the same way the first time anything read the column.
    /// </summary>
    public partial class AddRoleDataScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataScope",
                schema: "qmgr",
                table: "roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataScope",
                schema: "qmgr",
                table: "roles");
        }
    }
}
