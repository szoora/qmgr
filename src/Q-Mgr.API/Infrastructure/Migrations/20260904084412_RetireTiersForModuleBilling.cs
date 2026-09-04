using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Retires the tier system and moves recurring billing onto the modules a customer holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tier was not a leftover beside module pricing — it was the only billing engine in the
    /// product. A tier subscription was the one thing that produced a recurring invoice, while a
    /// module was charged once at purchase and never renewed. This migration is the data half of
    /// joining them: modules become the invoice lines, and the subscription row survives stripped
    /// of its plan as the organization's billing account.
    /// </para>
    /// <para>
    /// Order matters and the steps are not interchangeable. The grant reads
    /// <c>organizations."Tier"</c> and so must run before that column is dropped; the tier plan
    /// rows can only be deleted after <c>subscriptions."PlanId"</c> and its foreign key are gone.
    /// Every table name is schema-qualified: raw SQL does not inherit the model's default schema.
    /// </para>
    /// <para>
    /// It does not bill anybody for the past. Due dates are advanced from each module's original
    /// activation by whole cycles until they land in the future, so a customer keeps the
    /// anniversary they have always had and the first invoice covers the period ahead of them.
    /// </para>
    /// </remarks>
    public partial class RetireTiersForModuleBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------------------------
            // Data first, while Tier still exists.
            // ---------------------------------------------------------------------------------

            // 1. Nobody loses access. An organization on a paid tier that never bought a module was
            //    entitled through its tier alone; once the column is gone that entitlement has no
            //    source, so grant it the modules the tier implied. Granted, not sold: no charge and
            //    no agreed price, which is what GrantedByPlatformAdmin means everywhere else.
            migrationBuilder.Sql(@"
                INSERT INTO qmgr.organization_modules
                    (""Id"", ""OrganizationId"", ""ModuleId"", ""Status"", ""ActivatedAt"",
                     ""BillingCycle"", ""GrantedByPlatformAdmin"", ""AdminNote"", ""CreatedAt"", ""IsActive"")
                SELECT gen_random_uuid(), o.""Id"", m.""Id"", 1, now(), 0, true,
                       'Granted when the tier system was retired, to preserve the access the tier gave.',
                       now(), true
                FROM qmgr.organizations o
                CROSS JOIN qmgr.subscription_plans m
                WHERE o.""Tier"" <> 0
                  AND o.""Status"" <> 5
                  AND m.""Code"" IN ('core-queue','engagement-communications','visitor-management','student-welfare','integrations-api')
                  AND NOT EXISTS (
                      SELECT 1 FROM qmgr.organization_modules x
                      WHERE x.""OrganizationId"" = o.""Id"" AND x.""ModuleId"" = m.""Id"");
            ");

            // 2. Every billable module needs a due date. CurrentPeriodEnd was written at activation
            //    and read by nothing, so plenty of rows have none. The date is advanced from the
            //    original activation by whole cycles until it lands in the future: the customer
            //    keeps the anniversary they have always had, and is never billed for a period that
            //    has already elapsed.
            migrationBuilder.Sql(@"
                UPDATE qmgr.organization_modules om
                SET ""CurrentPeriodEnd"" = CASE
                        WHEN om.""BillingCycle"" = 1 THEN
                            om.""ActivatedAt"" + make_interval(years =>
                                GREATEST(1, DATE_PART('year', age(now(), om.""ActivatedAt""))::int + 1))
                        ELSE
                            om.""ActivatedAt"" + make_interval(months =>
                                GREATEST(1, (DATE_PART('year', age(now(), om.""ActivatedAt""))::int * 12
                                           + DATE_PART('month', age(now(), om.""ActivatedAt""))::int) + 1))
                    END
                WHERE om.""CurrentPeriodEnd"" IS NULL
                  AND om.""Status"" = 1
                  AND om.""GrantedByPlatformAdmin"" = false;
            ");

            migrationBuilder.DropForeignKey(
                name: "FK_subscriptions_subscription_plans_PlanId",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_subscriptions_PlanId",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "AgreedCurrency",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "AgreedUnitPrice",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "PlanId",
                schema: "qmgr",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "Tier",
                schema: "qmgr",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "Tier",
                schema: "qmgr",
                table: "organizations");

            // ---------------------------------------------------------------------------------
            // Now that PlanId is gone, the rest.
            // ---------------------------------------------------------------------------------

            // 3. A billing account for anybody who holds a module and has never had one. Until this
            //    change the only thing that opened one was subscribing to a tier, so an organization
            //    that only ever bought modules has none — and the invoice job needs one to hang the
            //    invoice off. Dated from its earliest activation, so the account's own history
            //    matches when the customer actually started.
            migrationBuilder.Sql(@"
                INSERT INTO qmgr.subscriptions
                    (""Id"", ""OrganizationId"", ""Status"", ""BillingCycle"", ""StartDate"",
                     ""CurrentPeriodStart"", ""CurrentPeriodEnd"", ""PreferredPaymentMethod"",
                     ""CancelAtPeriodEnd"", ""CreatedAt"", ""IsActive"")
                SELECT gen_random_uuid(), om.""OrganizationId"", 1, 0,
                       MIN(om.""ActivatedAt""), MIN(om.""ActivatedAt""), MIN(om.""ActivatedAt""),
                       1, false, now(), true
                FROM qmgr.organization_modules om
                WHERE om.""Status"" IN (0, 1)
                  AND NOT EXISTS (
                      SELECT 1 FROM qmgr.subscriptions s
                      WHERE s.""OrganizationId"" = om.""OrganizationId"")
                GROUP BY om.""OrganizationId"";
            ");

            // 4. The tier plan rows themselves, now that nothing points at them. They were never
            //    modules — no OrganizationModule references one — so this removes the four rows that
            //    made subscription_plans mean two different things at once.
            migrationBuilder.Sql(@"
                DELETE FROM qmgr.subscription_plans
                WHERE ""Code"" NOT IN ('core-queue','engagement-communications','visitor-management',
                                     'visitor-safeguarding','student-welfare','integrations-api');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                schema: "qmgr",
                table: "subscriptions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Tier",
                schema: "qmgr",
                table: "subscription_plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Tier",
                schema: "qmgr",
                table: "organizations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_PlanId",
                schema: "qmgr",
                table: "subscriptions",
                column: "PlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_subscriptions_subscription_plans_PlanId",
                schema: "qmgr",
                table: "subscriptions",
                column: "PlanId",
                principalSchema: "qmgr",
                principalTable: "subscription_plans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
