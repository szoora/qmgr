using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorWatchlistOverrideReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WatchlistOverrideReason",
                schema: "qmgr",
                table: "visitors",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // BACKFILL. Every override recorded before this column existed lives inside Notes as a
            // line reading "[Watchlist override] <reason>", written by ComposeCheckInNotes. Without
            // this, the compliance report's watchlist section would show an empty history for the
            // whole period before today and read as "no overrides ever happened" — which is a worse
            // answer than no report at all.
            //
            // The pattern is anchored to the exact prefix the code writes, and takes everything to
            // the end of that line. substring(... from ...) is Postgres POSIX regex capture: it
            // returns the first capturing group, or NULL when nothing matches, so rows with no
            // override are simply left null rather than filled with noise.
            migrationBuilder.Sql(@"
                UPDATE qmgr.visitors
                SET ""WatchlistOverrideReason"" = LEFT(TRIM(substring(""Notes"" from '\[Watchlist override\] ([^\n\r]*)')), 500)
                WHERE ""Notes"" LIKE '%[Watchlist override] %'
                  AND ""WatchlistOverrideReason"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WatchlistOverrideReason",
                schema: "qmgr",
                table: "visitors");
        }
    }
}
