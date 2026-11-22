using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Record No. with the unit before the discipline code: ITR-STAT-541-001 → ITR-541-STAT-001. Only the Record Nos. saved in the
    /// old order are rewritten (code with a letter, unit and number in digits); anything else typed by hand is left as it is.
    /// No model change.
    /// </summary>
    [DbContext(typeof(PresAppDbContext))]
    [Migration("20261122090000_Phase21RecordNoUnitFirst")]
    public partial class Phase21RecordNoUnitFirst : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(Swap(codeAt: 3, unitAt: 2, toCodeFirst: false));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(Swap(codeAt: 2, unitAt: 3, toCodeFirst: true));

        /// <summary>
        /// PARSENAME counts the parts from the right (4 = ITR, 1 = number) and gives NULL instead of failing, so values that are
        /// not ITR-x-y-n are never touched.
        /// </summary>
        private static string Swap(int codeAt, int unitAt, bool toCodeFirst)
        {
            var target = toCodeFirst
                ? "CONCAT('ITR-', UPPER(p.Code), '-', p.Unit, '-', p.Num)"
                : "CONCAT('ITR-', p.Unit, '-', UPPER(p.Code), '-', p.Num)";
            return $"""
                UPDATE e SET [RecordNo] = {target}
                FROM [GateFormEntry] e
                CROSS APPLY (SELECT REPLACE(e.[RecordNo], '-', '.') AS Dotted) d
                CROSS APPLY (SELECT PARSENAME(d.Dotted, 4) AS Start, PARSENAME(d.Dotted, {codeAt}) AS Code,
                                    PARSENAME(d.Dotted, {unitAt}) AS Unit, PARSENAME(d.Dotted, 1) AS Num) p
                WHERE e.[RecordNo] IS NOT NULL
                  AND e.[RecordNo] NOT LIKE '%[^A-Za-z0-9-]%'
                  AND LEN(e.[RecordNo]) - LEN(REPLACE(e.[RecordNo], '-', '')) = 3
                  AND p.Start = 'ITR'
                  AND p.Code LIKE '%[A-Za-z]%'
                  AND p.Unit <> '' AND p.Unit NOT LIKE '%[^0-9]%'
                  AND p.Num <> '' AND p.Num NOT LIKE '%[^0-9]%'
                """;
        }
    }
}
