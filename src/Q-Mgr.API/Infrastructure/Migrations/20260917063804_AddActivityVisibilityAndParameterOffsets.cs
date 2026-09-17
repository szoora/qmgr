using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityVisibilityAndParameterOffsets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OffsetsParameterId",
                schema: "qmgr",
                table: "PerformanceParameters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                schema: "qmgr",
                table: "ActivityEvents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // BACKFILL, in the same migration — the column exists to keep "Restricted record viewed" off the
            // subject's own trail, and every event already written would otherwise read as Standard and leak.
            //
            // 1. Events about a staff record take that record's current rung.
            migrationBuilder.Sql(@"
UPDATE qmgr.""ActivityEvents"" e
   SET ""Visibility"" = r.""Visibility""
  FROM qmgr.""StaffPerformanceRecords"" r
 WHERE e.""EntityType"" = 'StaffPerformanceRecord' AND e.""EntityId"" = r.""Id"";");

            // 2. A visibility change takes the HIGHER of its two rungs (the logger's rule), from the stored
            //    detail — written as numbers by System.Text.Json, but names are handled too.
            migrationBuilder.Sql(@"
UPDATE qmgr.""ActivityEvents""
   SET ""Visibility"" = GREATEST(""Visibility"",
        CASE WHEN (""DetailJson""::jsonb->>'From') ~ '^[0-9]+$' THEN (""DetailJson""::jsonb->>'From')::int
             WHEN (""DetailJson""::jsonb->>'From') = 'Restricted' THEN 2
             WHEN (""DetailJson""::jsonb->>'From') = 'Confidential' THEN 1 ELSE 0 END,
        CASE WHEN (""DetailJson""::jsonb->>'To') ~ '^[0-9]+$' THEN (""DetailJson""::jsonb->>'To')::int
             WHEN (""DetailJson""::jsonb->>'To') = 'Restricted' THEN 2
             WHEN (""DetailJson""::jsonb->>'To') = 'Confidential' THEN 1 ELSE 0 END)
 WHERE ""Action"" = 'staff.record.visibility-changed' AND ""DetailJson"" IS NOT NULL;");

            // 3. Belt and braces: any summary the logger wrote at the Restricted rung.
            migrationBuilder.Sql(@"
UPDATE qmgr.""ActivityEvents"" SET ""Visibility"" = 2
 WHERE ""Visibility"" < 2 AND ""Summary"" LIKE 'Restricted record %';");

            // 4. Appraisal events are Confidential by nature, and the three that carried a rating in plain
            //    text lose it (the rating stays in DetailJson, which no endpoint returns).
            migrationBuilder.Sql(@"
UPDATE qmgr.""ActivityEvents"" SET ""Visibility"" = GREATEST(""Visibility"", 1) WHERE ""EntityType"" = 'StaffAppraisal';
UPDATE qmgr.""ActivityEvents"" SET ""Summary"" = regexp_replace(""Summary"", ' appraisal reviewed: rating .*$', ' appraisal reviewed by the appraiser')
 WHERE ""Action"" = 'staff.appraisal.reviewed';
UPDATE qmgr.""ActivityEvents"" SET ""Summary"" = regexp_replace(""Summary"", ' appraisal moderated: final rating [^,]*(, changed from the appraiser.s .*)?$', ' appraisal moderated')
 WHERE ""Action"" = 'staff.appraisal.moderated';
UPDATE qmgr.""ActivityEvents"" SET ""Summary"" = regexp_replace(""Summary"", ' appraisal signed: rating .*$', ' appraisal signed; score frozen')
 WHERE ""Action"" = 'staff.appraisal.signed';");

            // 5. The seeded Lesson Recovery parameter offsets Lesson Attendance, as its purpose always said.
            migrationBuilder.Sql(@"
UPDATE qmgr.""PerformanceParameters"" rec
   SET ""OffsetsParameterId"" = att.""Id""
  FROM qmgr.""PerformanceParameters"" att
 WHERE rec.""OrganizationId"" = att.""OrganizationId""
   AND rec.""Name"" = 'Lesson Recovery' AND att.""Name"" = 'Lesson Attendance'
   AND att.""Kind"" IN (0, 1)
   AND rec.""OffsetsParameterId"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OffsetsParameterId",
                schema: "qmgr",
                table: "PerformanceParameters");

            migrationBuilder.DropColumn(
                name: "Visibility",
                schema: "qmgr",
                table: "ActivityEvents");
        }
    }
}
