using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase19RecordNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecordNoCode",
                table: "Discipline",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecordNoSequence",
                columns: table => new
                {
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    UnitNumber = table.Column<int>(type: "int", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordNoSequence", x => new { x.Code, x.UnitNumber });
                });

            migrationBuilder.CreateIndex(
                name: "IX_GateFormEntry_RecordNo",
                table: "GateFormEntry",
                column: "RecordNo");

            // Codes agreed with the client (Nov 2026); an admin changes them in Admin → Gate forms.
            migrationBuilder.Sql("UPDATE [Discipline] SET [RecordNoCode] = 'STAT' WHERE [Name] = 'Static'");
            migrationBuilder.Sql("UPDATE [Discipline] SET [RecordNoCode] = 'ELEC' WHERE [Name] = 'Electrical'");
            migrationBuilder.Sql("UPDATE [Discipline] SET [RecordNoCode] = 'PIP' WHERE [Name] = 'Piping'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordNoSequence");

            migrationBuilder.DropIndex(
                name: "IX_GateFormEntry_RecordNo",
                table: "GateFormEntry");

            migrationBuilder.DropColumn(
                name: "RecordNoCode",
                table: "Discipline");
        }
    }
}
