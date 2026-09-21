using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QMgr.Infrastructure.Data;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Two owner decisions of 2026-09-19, applied to existing rows. Data only — no schema change — so
    /// the model snapshot does not move and there is no designer file.
    ///
    /// 1. **The default currency is UGX, not USD** ("btw default currency should be UGX not USD"). Every
    ///    module is sold in UGX and the sacc.ug gateway collects only UGX, yet an organization defaulted
    ///    to USD, so a renewal invoice was raised in dollars and Mobile Money would have charged the
    ///    dollar figure as shillings. An organization moves to UGX only where it never chose USD in any
    ///    way that matters: no module held at a USD agreed price and no USD invoice on record. Anything
    ///    already billed in dollars is left exactly as it is.
    ///
    /// 2. **Stripe stays off unless it is actually configured** ("keep stripe disabled"). Its switch
    ///    defaulted to on, so installs that never set a key read "Enabled: Yes" and offered tenants a
    ///    card option that could only fail. A Stripe row with no secret key is switched off; one with a
    ///    key is an administrator's choice and is not touched. The key may be stored PascalCase (the
    ///    seeder) or camelCase (the settings editor), so both spellings are read and the camelCase
    ///    switch is removed before the PascalCase one is written.
    /// </summary>
    [DbContext(typeof(QMgrDbContext))]
    [Migration("20260919160000_UgxDefaultAndStripeOff")]
    public partial class UgxDefaultAndStripeOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE qmgr.organizations o
   SET ""PreferredCurrency"" = 'UGX'
 WHERE COALESCE(o.""PreferredCurrency"", 'USD') = 'USD'
   AND NOT EXISTS (SELECT 1 FROM qmgr.organization_modules m
                    WHERE m.""OrganizationId"" = o.""Id"" AND m.""AgreedPriceUsd"" IS NOT NULL)
   AND NOT EXISTS (SELECT 1 FROM qmgr.invoices i
                    WHERE i.""OrganizationId"" = o.""Id"" AND i.""Currency"" = 'USD');
");

            migrationBuilder.Sql(@"
UPDATE qmgr.""PlatformSettings""
   SET ""SettingsJson"" = ((""SettingsJson""::jsonb - 'enabled') || '{""Enabled"": false}'::jsonb)::text
 WHERE ""Category"" = 'Stripe'
   AND COALESCE(NULLIF(""SettingsJson""::jsonb ->> 'SecretKey', ''),
                NULLIF(""SettingsJson""::jsonb ->> 'secretKey', '')) IS NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Which organizations were USD before cannot be told apart from those
            // that were never asked, and switching Stripe back on without a key would only restore a
            // card option that cannot work.
        }
    }
}
