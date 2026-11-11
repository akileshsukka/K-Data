using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase16CategoryScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DashboardCategory_UnitId_Label",
                table: "DashboardCategory");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "DashboardCategory",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Mechanical");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardCategory_UnitId_Scope_Label",
                table: "DashboardCategory",
                columns: new[] { "UnitId", "Scope", "Label" },
                unique: true);

            // E&I items that already have a (mechanical) category keep it, in the new E&I list: each such category is
            // copied there with the same description and notes, code Cat3 → CatE3, and the E&I items move to the copy.
            migrationBuilder.Sql("""
                INSERT INTO DashboardCategory (UnitId, Section, Scope, Label, Code, Priority, SortOrder, ActionBy, EmailAction, Recommendation, Note, IsActive)
                SELECT c.UnitId, c.Section, 'ElectricalInstrument', c.Label,
                       CASE WHEN c.Code LIKE 'Cat[0-9]%' THEN 'CatE' + SUBSTRING(c.Code, 4, 6) ELSE NULL END,
                       c.Priority, c.SortOrder, c.ActionBy, c.EmailAction, c.Recommendation, c.Note, c.IsActive
                FROM DashboardCategory c
                WHERE c.Scope = 'Mechanical'
                  AND EXISTS (SELECT 1 FROM AssetPreservation p JOIN Asset a ON a.Id = p.AssetId
                              WHERE p.DashboardCategoryId = c.Id AND a.Type = 'ElectricalInstrument');

                UPDATE p SET DashboardCategoryId = e.Id
                FROM AssetPreservation p
                JOIN Asset a ON a.Id = p.AssetId AND a.Type = 'ElectricalInstrument'
                JOIN DashboardCategory c ON c.Id = p.DashboardCategoryId AND c.Scope = 'Mechanical'
                JOIN DashboardCategory e ON e.UnitId = c.UnitId AND e.Scope = 'ElectricalInstrument' AND e.Label = c.Label;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one list: E&I items return to the mechanical category with the same description, if any.
            migrationBuilder.Sql("""
                UPDATE p SET DashboardCategoryId = m.Id
                FROM AssetPreservation p
                JOIN DashboardCategory e ON e.Id = p.DashboardCategoryId AND e.Scope = 'ElectricalInstrument'
                LEFT JOIN DashboardCategory m ON m.UnitId = e.UnitId AND m.Scope = 'Mechanical' AND m.Label = e.Label;
                DELETE FROM DashboardCategory WHERE Scope = 'ElectricalInstrument';
                """);

            migrationBuilder.DropIndex(
                name: "IX_DashboardCategory_UnitId_Scope_Label",
                table: "DashboardCategory");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "DashboardCategory");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardCategory_UnitId_Label",
                table: "DashboardCategory",
                columns: new[] { "UnitId", "Label" },
                unique: true);
        }
    }
}
