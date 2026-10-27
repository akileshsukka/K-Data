using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4PipeSpecs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insulation type is no longer kept (neither on the lines nor in the line codes).
            migrationBuilder.Sql("DELETE FROM LineCode WHERE Kind IN ('InsulationType', 'InsulationMaterial')");

            migrationBuilder.DropColumn(
                name: "Insulation",
                table: "PipelineDetail");

            migrationBuilder.AddColumn<string>(
                name: "PidNumber",
                table: "PipelineDetail",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PipeSpec",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PressureClass = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    FlangeType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Material = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipeSpec", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipeSpec_Code",
                table: "PipeSpec",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipeSpec");

            migrationBuilder.DropColumn(
                name: "PidNumber",
                table: "PipelineDetail");

            migrationBuilder.AddColumn<string>(
                name: "Insulation",
                table: "PipelineDetail",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }
    }
}
