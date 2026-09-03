using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                schema: "qmgr",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPhone",
                schema: "qmgr",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhoneVerifiedAt",
                schema: "qmgr",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameBlockingKey",
                schema: "qmgr",
                table: "organizations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                schema: "qmgr",
                table: "organizations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "registration_attempts",
                schema: "qmgr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    NormalizedPhone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PhoneWasVerified = table.Column<bool>(type: "boolean", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NameBlockingKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ContactFirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ContactLastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClientAddressHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    RiskScore = table.Column<int>(type: "integer", nullable: false),
                    Signals = table.Column<string>(type: "text", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchedOrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReviewOutcome = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registration_attempts", x => x.Id);
                });

            // Existing rows have to carry the canonical values before the unique index can be
            // built: every user row currently holds the column default, and Postgres will not
            // accept a unique index over a column where every row reads the same empty string.
            // The SQL below mirrors RegistrationIdentity's C# normalization step for step. It is
            // deliberately written as a short sequence of plain updates rather than one clever
            // expression, so a reader can check each step against the C# it copies.

            // 1. Baseline: the trimmed, lower-cased address, which is what the C# returns for any
            //    provider it does not recognise.
            migrationBuilder.Sql(@"UPDATE qmgr.users SET ""NormalizedEmail"" = lower(btrim(""Email""));");

            // 2. Fold a plus tag away, but only for the providers that actually alias on it.
            migrationBuilder.Sql(@"
                UPDATE qmgr.users
                SET ""NormalizedEmail"" =
                    left(""NormalizedEmail"", position('+' in ""NormalizedEmail"") - 1)
                    || '@' || split_part(""NormalizedEmail"", '@', 2)
                WHERE length(""NormalizedEmail"") - length(replace(""NormalizedEmail"", '@', '')) = 1
                  AND position('+' in split_part(""NormalizedEmail"", '@', 1)) > 0
                  AND split_part(""NormalizedEmail"", '@', 2) IN (
                        'gmail.com', 'googlemail.com',
                        'outlook.com', 'hotmail.com', 'live.com', 'msn.com',
                        'yahoo.com', 'ymail.com', 'protonmail.com', 'proton.me',
                        'icloud.com', 'me.com', 'fastmail.com', 'zoho.com');");

            // 3. The two Gmail hostnames are one mailbox, so collapse them onto the canonical one.
            migrationBuilder.Sql(@"
                UPDATE qmgr.users
                SET ""NormalizedEmail"" = split_part(""NormalizedEmail"", '@', 1) || '@gmail.com'
                WHERE split_part(""NormalizedEmail"", '@', 2) = 'googlemail.com';");

            // 4. Gmail ignores dots in the local part.
            migrationBuilder.Sql(@"
                UPDATE qmgr.users
                SET ""NormalizedEmail"" = replace(split_part(""NormalizedEmail"", '@', 1), '.', '') || '@gmail.com'
                WHERE split_part(""NormalizedEmail"", '@', 2) = 'gmail.com';");

            // 5. An address whose local part folds away entirely is not an identity. Keep the
            //    original rather than collapsing every such row onto one key, exactly as the C#
            //    does. The Id suffix covers the pathological case of a blank stored address, which
            //    would otherwise make the unique index unbuildable.
            migrationBuilder.Sql(@"
                UPDATE qmgr.users
                SET ""NormalizedEmail"" = lower(btrim(""Email""))
                WHERE ""NormalizedEmail"" LIKE '@%';");
            migrationBuilder.Sql(@"
                UPDATE qmgr.users
                SET ""NormalizedEmail"" = 'unknown-' || ""Id""::text
                WHERE btrim(coalesce(""NormalizedEmail"", '')) = '';");

            // Phone numbers: digits only, local leading zero swapped for the country code, and an
            // international 00 prefix dropped.
            migrationBuilder.Sql(@"
                UPDATE qmgr.users AS u
                SET ""NormalizedPhone"" = CASE
                    WHEN s.d = '' THEN NULL
                    WHEN s.d LIKE '00%' THEN NULLIF(substring(s.d from 3), '')
                    WHEN s.d LIKE '0%' THEN '256' || ltrim(s.d, '0')
                    ELSE s.d END
                FROM (
                    SELECT ""Id"" AS uid, regexp_replace(coalesce(""Phone"", ''), '[^0-9]', '', 'g') AS d
                    FROM qmgr.users
                ) AS s
                WHERE s.uid = u.""Id"";");

            // Organization names: lower-cased, punctuation flattened, legal-form and filler words
            // dropped, then the remaining words sorted so word order stops mattering. The C
            // collation makes Postgres sort byte-wise, which is what StringComparer.Ordinal does on
            // the C# side; a locale-aware sort would order the words differently and produce keys
            // that never match the ones written at sign-up.
            migrationBuilder.Sql(@"
                UPDATE qmgr.organizations AS o
                SET ""NormalizedName"" = coalesce(NULLIF(s.joined, ''), NULLIF(s.fallback, ''))
                FROM (
                    SELECT
                        org.""Id"" AS oid,
                        (SELECT string_agg(w, ' ' ORDER BY w COLLATE ""C"")
                         FROM unnest(string_to_array(
                                btrim(regexp_replace(lower(btrim(org.""Name"")), '[^a-z0-9]+', ' ', 'g')), ' ')) AS w
                         WHERE w <> '' AND w NOT IN (
                            'ltd','limited','plc','inc','incorporated','llc','co','company',
                            'corp','corporation','enterprise','enterprises','holdings','group',
                            'services','service','solutions','sacco','and','the','of')) AS joined,
                        replace(btrim(regexp_replace(lower(btrim(org.""Name"")), '[^a-z0-9]+', ' ', 'g')), ' ', '') AS fallback
                    FROM qmgr.organizations AS org
                ) AS s
                WHERE s.oid = o.""Id"";");

            // The blocking key is the first three characters of the first sorted word plus a
            // coarse length band, which is what narrows the candidate set at sign-up.
            migrationBuilder.Sql(@"
                UPDATE qmgr.organizations
                SET ""NameBlockingKey"" =
                    left(split_part(""NormalizedName"", ' ', 1), 3) || ':'
                    || (length(replace(""NormalizedName"", ' ', '')) / 4)::text
                WHERE ""NormalizedName"" IS NOT NULL AND ""NormalizedName"" <> '';");

            migrationBuilder.CreateIndex(
                name: "idx_users_normalized_email_unique",
                schema: "qmgr",
                table: "users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_users_normalized_phone",
                schema: "qmgr",
                table: "users",
                column: "NormalizedPhone");

            migrationBuilder.CreateIndex(
                name: "idx_organizations_name_blocking_key",
                schema: "qmgr",
                table: "organizations",
                column: "NameBlockingKey");

            migrationBuilder.CreateIndex(
                name: "idx_organizations_normalized_name",
                schema: "qmgr",
                table: "organizations",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "idx_registration_attempts_address_time",
                schema: "qmgr",
                table: "registration_attempts",
                columns: new[] { "ClientAddressHash", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_registration_attempts_decision_reviewed",
                schema: "qmgr",
                table: "registration_attempts",
                columns: new[] { "Decision", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "idx_registration_attempts_normalized_email",
                schema: "qmgr",
                table: "registration_attempts",
                column: "NormalizedEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "registration_attempts",
                schema: "qmgr");

            migrationBuilder.DropIndex(
                name: "idx_users_normalized_email_unique",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropIndex(
                name: "idx_users_normalized_phone",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropIndex(
                name: "idx_organizations_name_blocking_key",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropIndex(
                name: "idx_organizations_normalized_name",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "NormalizedPhone",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PhoneVerifiedAt",
                schema: "qmgr",
                table: "users");

            migrationBuilder.DropColumn(
                name: "NameBlockingKey",
                schema: "qmgr",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                schema: "qmgr",
                table: "organizations");
        }
    }
}
