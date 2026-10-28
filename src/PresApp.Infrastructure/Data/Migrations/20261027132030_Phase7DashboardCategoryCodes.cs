using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase7DashboardCategoryCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "DashboardCategory",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Recommendation",
                table: "DashboardCategory",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DashboardCategoryId",
                table: "AssetPreservation",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadyForPreservation",
                table: "AssetPreservation",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            // Existing categories get their code from the label ("Cat1 :…", "Cat 2: …"). Rows without a number,
            // or repeating one already taken in the same unit and section, get the next free number.
            // The labels are not changed: the v3 export still matches them against the template.
            // EXEC: the column is new, so the statement is compiled only when it runs (also in a generated script).
            migrationBuilder.Sql("""
                EXEC(N'
                ;WITH parsed AS (
                    SELECT Id, UnitId, Section, SortOrder,
                           CASE WHEN LTRIM(Label) LIKE ''Cat%'' THEN LTRIM(SUBSTRING(LTRIM(Label), 4, 200)) END AS Rest
                    FROM DashboardCategory
                ), numbered AS (
                    SELECT Id, UnitId, Section, SortOrder,
                           TRY_CAST(NULLIF(LEFT(Rest, PATINDEX(''%[^0-9]%'', Rest + ''x'') - 1), '''') AS int) AS N
                    FROM parsed
                ), ranked AS (
                    SELECT Id, N, ROW_NUMBER() OVER (PARTITION BY UnitId, Section, N ORDER BY SortOrder, Id) AS Dup
                    FROM numbered
                )
                UPDATE d SET Code = CONCAT(''Cat'', r.N)
                FROM DashboardCategory d JOIN ranked r ON r.Id = d.Id
                WHERE r.N > 0 AND r.Dup = 1;
                ');
                """);

            migrationBuilder.Sql("""
                EXEC(N'
                ;WITH used AS (
                    SELECT UnitId, Section, MAX(TRY_CAST(SUBSTRING(Code, 4, 10) AS int)) AS MaxN
                    FROM DashboardCategory WHERE Code IS NOT NULL GROUP BY UnitId, Section
                ), todo AS (
                    SELECT Id, UnitId, Section, ROW_NUMBER() OVER (PARTITION BY UnitId, Section ORDER BY SortOrder, Id) AS Rn
                    FROM DashboardCategory WHERE Code IS NULL
                )
                UPDATE d SET Code = CONCAT(''Cat'', ISNULL(u.MaxN, 0) + t.Rn)
                FROM DashboardCategory d JOIN todo t ON t.Id = d.Id
                LEFT JOIN used u ON u.UnitId = t.UnitId AND u.Section = t.Section;
                ');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DashboardCategory_UnitId_Section_Code",
                table: "DashboardCategory",
                columns: new[] { "UnitId", "Section", "Code" },
                unique: true,
                filter: "[Code] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssetPreservation_DashboardCategoryId",
                table: "AssetPreservation",
                column: "DashboardCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_AssetPreservation_DashboardCategory_DashboardCategoryId",
                table: "AssetPreservation",
                column: "DashboardCategoryId",
                principalTable: "DashboardCategory",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssetPreservation_DashboardCategory_DashboardCategoryId",
                table: "AssetPreservation");

            migrationBuilder.DropIndex(
                name: "IX_DashboardCategory_UnitId_Section_Code",
                table: "DashboardCategory");

            migrationBuilder.DropIndex(
                name: "IX_AssetPreservation_DashboardCategoryId",
                table: "AssetPreservation");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "DashboardCategory");

            migrationBuilder.DropColumn(
                name: "Recommendation",
                table: "DashboardCategory");

            migrationBuilder.DropColumn(
                name: "DashboardCategoryId",
                table: "AssetPreservation");

            migrationBuilder.DropColumn(
                name: "ReadyForPreservation",
                table: "AssetPreservation");
        }
    }
}
