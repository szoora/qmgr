using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Batch operations, and deliberately only two columns.
    ///
    /// A bulk edit has rows, a per-row outcome, progress, counts and a history — precisely what
    /// RosterImportJob and RosterImportJobEntry already model, which is why this adds a third
    /// RosterImportKind rather than a third table, following the standing enhance-before-you-add
    /// rule. Both new enum values (RosterImportKind.Batch, RosterImportRowOutcome.Skipped) are
    /// stored as integers and appended, never inserted, so neither needs any DDL at all.
    ///
    /// PreviousValue and NewValue are what make a batch reversible. Without the old value,
    /// reversing a mistaken promotion means editing eight hundred rows by hand — which is the
    /// difference between an operation an administrator will run against a whole school and one
    /// they will not. They also make the log readable a term later: "S3 → S4" says what happened
    /// where "Updated" does not. Nullable because an import changes many fields at once and has
    /// no single before-and-after to record.
    /// </summary>
    public partial class AddBatchOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewValue",
                schema: "qmgr",
                table: "roster_import_job_entries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousValue",
                schema: "qmgr",
                table: "roster_import_job_entries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewValue",
                schema: "qmgr",
                table: "roster_import_job_entries");

            migrationBuilder.DropColumn(
                name: "PreviousValue",
                schema: "qmgr",
                table: "roster_import_job_entries");
        }
    }
}
