using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipeSpecGasket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Material",
                table: "PipeSpec",
                newName: "Gasket");

            // RFD / RFC are kept as RF (the gasket tells them apart).
            migrationBuilder.Sql("UPDATE PipeSpec SET FlangeType = 'RF' WHERE FlangeType LIKE 'RF%' AND FlangeType <> 'RF'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Gasket",
                table: "PipeSpec",
                newName: "Material");
        }
    }
}
