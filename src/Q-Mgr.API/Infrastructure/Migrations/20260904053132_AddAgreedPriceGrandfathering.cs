using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Records what a customer agreed to pay, so that editing a price in the catalog stops
    /// repricing the people already on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The columns are nullable and null means "track the list price", which is exactly how every
    /// row behaved before they existed — so the schema change alone is a no-op. The backfill below
    /// is what actually decides something: it freezes every current customer at today's price, so
    /// the very first edit made in the new catalog editor cannot reprice them. Without it the
    /// feature would only protect customers who signed up after this deployment.
    /// </para>
    /// <para>
    /// Both statements mirror the C# capture rules exactly, and both must stay mirrored:
    /// <c>BillingService.CreateSubscriptionAsync</c> prices by the organization's preferred
    /// currency and the subscription's cycle, and <c>ModuleAccessService.ActivateAsync</c> captures
    /// both currencies for the module row's cycle. Enum columns are integers here —
    /// <c>SubscriptionStatus</c> Trialing/Active/PastDue are 0/1/2, <c>OrganizationModuleStatus</c>
    /// the same, and <c>BillingCycle</c> Annual is 1.
    /// </para>
    /// <para>
    /// Trialing module rows are deliberately left null: no price has been agreed on a trial, and
    /// activation captures one when it converts. Platform-admin grants are left null too, because
    /// nothing was ever charged for them.
    /// </para>
    /// <para>
    /// Every table name is schema-qualified. Raw SQL does not inherit the model's default schema,
    /// and <c>search_path</c> here does not include <c>qmgr</c>.
    /// </para>
    /// </remarks>
    public partial class AddAgreedPriceGrandfathering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgreedCurrency",
                schema: "qmgr",
                table: "subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AgreedUnitPrice",
                schema: "qmgr",
                table: "subscriptions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AgreedPriceUgx",
                schema: "qmgr",
                table: "organization_modules",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AgreedPriceUsd",
                schema: "qmgr",
                table: "organization_modules",
                type: "numeric",
                nullable: true);

            // Freeze every live subscription at the price its plan charges today.
            migrationBuilder.Sql(@"
                UPDATE qmgr.subscriptions s
                SET ""AgreedCurrency"" = o.""PreferredCurrency"",
                    ""AgreedUnitPrice"" = CASE
                        WHEN s.""BillingCycle"" = 1 THEN
                            CASE WHEN upper(o.""PreferredCurrency"") = 'UGX'
                                 THEN p.""AnnualPriceUgx"" ELSE p.""AnnualPriceUsd"" END
                        ELSE
                            CASE WHEN upper(o.""PreferredCurrency"") = 'UGX'
                                 THEN p.""MonthlyPriceUgx"" ELSE p.""MonthlyPriceUsd"" END
                    END
                FROM qmgr.organizations o, qmgr.subscription_plans p
                WHERE o.""Id"" = s.""OrganizationId""
                  AND p.""Id"" = s.""PlanId""
                  AND s.""Status"" IN (0, 1, 2);
            ");

            // And every paid module hold, in both currencies, at the price for its own cycle.
            migrationBuilder.Sql(@"
                UPDATE qmgr.organization_modules om
                SET ""AgreedPriceUgx"" = CASE WHEN om.""BillingCycle"" = 1
                                              THEN m.""AnnualPriceUgx"" ELSE m.""MonthlyPriceUgx"" END,
                    ""AgreedPriceUsd"" = CASE WHEN om.""BillingCycle"" = 1
                                              THEN m.""AnnualPriceUsd"" ELSE m.""MonthlyPriceUsd"" END
                FROM qmgr.subscription_plans m
                WHERE m.""Id"" = om.""ModuleId""
                  AND om.""Status"" IN (1, 2)
                  AND om.""GrantedByPlatformAdmin"" = false;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreedCurrency",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "AgreedUnitPrice",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "AgreedPriceUgx",
                schema: "qmgr",
                table: "organization_modules");

            migrationBuilder.DropColumn(
                name: "AgreedPriceUsd",
                schema: "qmgr",
                table: "organization_modules");
        }
    }
}
