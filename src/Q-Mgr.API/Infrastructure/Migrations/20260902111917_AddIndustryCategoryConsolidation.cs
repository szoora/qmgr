using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QMgr.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only migration (no schema/column change — QMgr.Domain.Enums.IndustryType is still a
    /// plain int-backed column on both tables) for the enum consolidation from 9 narrow verticals
    /// down to 5 broader categories: Service=0, Business=1, Health=2, Education=3,
    /// Communications=4. Remaps every existing stored int on Organization.IndustryType and
    /// DocArticle.Industry from the old numbering (General=0, Hospital=1, Bank=2, Pharmacy=3,
    /// Retail=4, Government=5, Telecom=6, Restaurant=7, School=8) to the new one. Without this,
    /// any pre-existing row would silently hold an integer that no longer names any real enum
    /// member (e.g. old Restaurant=7 has no meaning under the new 0-4 range) — not a crash, since
    /// C# doesn't validate enum ints, but a real data-integrity problem: the value would render as
    /// a raw number wherever the UI expects a label, and none of the industry-picker dropdowns
    /// would show it as selected.
    /// </summary>
    /// <inheritdoc />
    public partial class AddIndustryCategoryConsolidation : Migration
    {
        // Old -> new: General(0)->Service(0), Hospital(1)->Health(2), Bank(2)->Business(1),
        // Pharmacy(3)->Health(2), Retail(4)->Service(0), Government(5)->Business(1),
        // Telecom(6)->Service(0), Restaurant(7)->Service(0), School(8)->Education(3).
        // New Communications(4) has no old value mapped into it — a genuinely new category.
        private const string RemapCase = """
            CASE "{0}"
                WHEN 0 THEN 0
                WHEN 1 THEN 2
                WHEN 2 THEN 1
                WHEN 3 THEN 2
                WHEN 4 THEN 0
                WHEN 5 THEN 1
                WHEN 6 THEN 0
                WHEN 7 THEN 0
                WHEN 8 THEN 3
                ELSE "{0}"
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""UPDATE qmgr.organizations SET "IndustryType" = {string.Format(RemapCase, "IndustryType")};""");
            migrationBuilder.Sql($"""UPDATE qmgr.doc_articles SET "Industry" = {string.Format(RemapCase, "Industry")} WHERE "Industry" IS NOT NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy by construction — 4 old categories collapsed into "Service" (General, Retail,
            // Telecom, Restaurant) and 2 into each of "Business" (Bank, Government) and "Health"
            // (Hospital, Pharmacy), so which specific old value a row had is gone. This picks one
            // arbitrary representative per new category (General for Service, Bank for Business,
            // Hospital for Health, School for Education) purely so a rollback leaves valid old-range
            // ints rather than nonsense — it does NOT restore the original per-org categorization.
            // Communications(4) has no old equivalent at all and maps back to General(0).
            const string reverseCase = """
                CASE "{0}"
                    WHEN 0 THEN 0
                    WHEN 1 THEN 2
                    WHEN 2 THEN 1
                    WHEN 3 THEN 8
                    WHEN 4 THEN 0
                    ELSE "{0}"
                END
                """;

            migrationBuilder.Sql($"""UPDATE qmgr.organizations SET "IndustryType" = {string.Format(reverseCase, "IndustryType")};""");
            migrationBuilder.Sql($"""UPDATE qmgr.doc_articles SET "Industry" = {string.Format(reverseCase, "Industry")} WHERE "Industry" IS NOT NULL;""");
        }
    }
}
