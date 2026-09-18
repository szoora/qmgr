using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// "Engagement &amp; Communications" becomes "Communication" (user decision, 2026-09-18). Data only;
    /// no schema change, which is why the scaffold came out empty.
    ///
    /// The CODE 'engagement-communications' is a stored wire format — every [RequireModule], every
    /// ModuleRouteMap entry and every organization's hold reads it — so it does not move. Only what a
    /// person reads does. The module holds signage, broadcasts, feedback and the shared-document
    /// Library: all of it communication, outbound or in. "Engagement" described only the feedback half
    /// and is the marketing word for it, which is the same objection that retired "Tenant Admin".
    ///
    /// The name changes only where the row still holds the value it shipped with, exactly as the
    /// Student Welfare rename did on 2026-09-17: the Module Catalog editor owns a row once an
    /// administrator has changed it, and a migration must not silently undo that.
    ///
    /// It is needed at all because ModuleCatalogDefaults is insert-if-missing only, so an existing
    /// install would never pick a new name up from the seeder — the trap the Administrator role rename
    /// hit earlier the same day.
    /// </summary>
    public partial class RenameEngagementModuleToCommunication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE qmgr.subscription_plans SET ""Name"" = 'Communication'
 WHERE ""Code"" = 'engagement-communications' AND ""Name"" = 'Engagement & Communications';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE qmgr.subscription_plans SET ""Name"" = 'Engagement & Communications'
 WHERE ""Code"" = 'engagement-communications' AND ""Name"" = 'Communication';
");
        }
    }
}
