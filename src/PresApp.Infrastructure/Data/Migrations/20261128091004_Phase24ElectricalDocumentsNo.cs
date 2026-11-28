using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Unit ID removed from the gate forms (no form prints it); the documents number of the electrical form added per unit.
    /// A new column, not a rename of DefaultUnitId: its values ("541") are not documents numbers.
    /// </summary>
    public partial class Phase24ElectricalDocumentsNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitIdText",
                table: "GateFormEntry");

            migrationBuilder.DropColumn(
                name: "DefaultUnitId",
                table: "GateFormUnitSetting");

            migrationBuilder.AddColumn<string>(
                name: "ElectricalDocumentsNo",
                table: "GateFormUnitSetting",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ElectricalDocumentsNo",
                table: "GateFormUnitSetting");

            migrationBuilder.AddColumn<string>(
                name: "DefaultUnitId",
                table: "GateFormUnitSetting",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitIdText",
                table: "GateFormEntry",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }
    }
}
