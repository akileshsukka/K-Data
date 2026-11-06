using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase15LineCodeUnitExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LineCode_Kind_Code",
                table: "LineCode");

            migrationBuilder.AddColumn<int>(
                name: "UnitId",
                table: "LineCode",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LineCode_Kind_Code_UnitId",
                table: "LineCode",
                columns: new[] { "Kind", "Code", "UnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LineCode_UnitId",
                table: "LineCode",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_LineCode_Unit_UnitId",
                table: "LineCode",
                column: "UnitId",
                principalTable: "Unit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LineCode_Unit_UnitId",
                table: "LineCode");

            migrationBuilder.DropIndex(
                name: "IX_LineCode_Kind_Code_UnitId",
                table: "LineCode");

            migrationBuilder.DropIndex(
                name: "IX_LineCode_UnitId",
                table: "LineCode");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "LineCode");

            migrationBuilder.CreateIndex(
                name: "IX_LineCode_Kind_Code",
                table: "LineCode",
                columns: new[] { "Kind", "Code" },
                unique: true);
        }
    }
}
