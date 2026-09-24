using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QMgr.Infrastructure.Data;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// The rebrand from Q-Mgr to SACC Dashboard (2026-09-24, plan docs/plans/SACC_DASHBOARD_REBRAND.md).
    /// Data only — no column changes, so there is no model snapshot change and no Designer file.
    ///
    /// <para>Every UPDATE touches a row ONLY while it still holds the value this product SHIPPED with,
    /// the rule FoldStaffPerformanceIntoWelfareModule established: once an administrator has typed their
    /// own value, it is theirs and a deploy must not silently undo it.</para>
    ///
    /// <para><b>The seeded organisations' brand names.</b> <c>Organization.BrandName</c> is now a school's
    /// app name, shown as typed. The platform organisation and the demo shipped with "Q-Mgr Platform" and
    /// "Q-Mgr Demo" — values that pass the name rule and would have been shown as an app name. Cleared,
    /// so both read as ours.</para>
    ///
    /// <para><b>The platform mailbox's display name.</b> <c>PlatformEmailDefaults</c> fills the Email row only
    /// when it is blank, so an existing install would have gone on sending as "Q-Mgr" for ever.</para>
    ///
    /// <para><b>A tenant's email From name left at the shipped default.</b> The notification settings form
    /// used to default the field to "Q-Mgr" (or show "Q-Mgr Queue System"), so most tenants saved it
    /// unchanged. Cleared to null, which the SMTP resolver reads as "the app's name". <b>SMS sender IDs are
    /// deliberately NOT touched</b>: the gateway may accept only a registered sender, and a rewritten one could
    /// stop every text a school sends.</para>
    /// </summary>
    [DbContext(typeof(QMgrDbContext))]
    [Migration("20260924090000_RebrandToSaccDashboard")]
    public partial class RebrandToSaccDashboard : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE qmgr.organizations SET ""BrandName"" = NULL
 WHERE ""BrandName"" IN ('Q-Mgr Platform', 'Q-Mgr Demo');");

            // SettingsJson is text holding a JSON object. Both casings are matched because the serializer's
            // naming policy has not been the same on every writer of this row.
            migrationBuilder.Sql(@"
UPDATE qmgr.""PlatformSettings""
   SET ""SettingsJson"" = jsonb_set(""SettingsJson""::jsonb, '{FromName}', '""SACC Dashboard""')::text
 WHERE ""Category"" = 'Email' AND ""SettingsJson""::jsonb ->> 'FromName' = 'Q-Mgr';
UPDATE qmgr.""PlatformSettings""
   SET ""SettingsJson"" = jsonb_set(""SettingsJson""::jsonb, '{fromName}', '""SACC Dashboard""')::text
 WHERE ""Category"" = 'Email' AND ""SettingsJson""::jsonb ->> 'fromName' = 'Q-Mgr';");

            migrationBuilder.Sql(@"
UPDATE qmgr.""NotificationSettings"" SET ""EmailFromName"" = NULL
 WHERE ""EmailFromName"" IN ('Q-Mgr', 'Q-Mgr Queue System');");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Put back only what Up can be sure it changed. The cleared brand names and From names cannot be
            // told apart from ones that were already empty, and inventing them back would be worse.
            migrationBuilder.Sql(@"
UPDATE qmgr.""PlatformSettings""
   SET ""SettingsJson"" = jsonb_set(""SettingsJson""::jsonb, '{FromName}', '""Q-Mgr""')::text
 WHERE ""Category"" = 'Email' AND ""SettingsJson""::jsonb ->> 'FromName' = 'SACC Dashboard';");
        }
    }
}
