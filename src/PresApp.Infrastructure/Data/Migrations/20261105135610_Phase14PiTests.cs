using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>The single PI test becomes six (U-V, V-W, U-W, U-G, V-G, W-G). The old PI values move to U-V, its place in the sheet.</summary>
    public partial class Phase14PiTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Pi10",
                table: "ElectricalTest",
                newName: "PiUV10");

            migrationBuilder.RenameColumn(
                name: "Pi1",
                table: "ElectricalTest",
                newName: "PiUV1");

            migrationBuilder.AddColumn<decimal>(
                name: "PiUG1",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiUG10",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiWG1",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiWG10",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiUW1",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiUW10",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiVG1",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiVG10",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiVW1",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PiVW10",
                table: "ElectricalTest",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PiUG1",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiUG10",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiWG1",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiWG10",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiUW1",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiUW10",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiVG1",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiVG10",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiVW1",
                table: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "PiVW10",
                table: "ElectricalTest");

            migrationBuilder.RenameColumn(
                name: "PiUV10",
                table: "ElectricalTest",
                newName: "Pi10");

            migrationBuilder.RenameColumn(
                name: "PiUV1",
                table: "ElectricalTest",
                newName: "Pi1");
        }
    }
}
