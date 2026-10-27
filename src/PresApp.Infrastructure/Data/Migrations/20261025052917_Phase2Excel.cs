using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Excel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExcelSession_UnitId",
                table: "ExcelSession");

            migrationBuilder.AddColumn<int>(
                name: "RowsInFile",
                table: "ImportRun",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AssetTag",
                table: "ImportChange",
                type: "nvarchar(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RowNumber",
                table: "ImportChange",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseJson",
                table: "ExcelSession",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "ExcelSession",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RowCount",
                table: "ExcelSession",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ExcelTemplate",
                columns: table => new
                {
                    UnitId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcelTemplate", x => x.UnitId);
                    table.ForeignKey(
                        name: "FK_ExcelTemplate_Unit_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Unit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportFile",
                columns: table => new
                {
                    ImportRunId = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportFile", x => x.ImportRunId);
                    table.ForeignKey(
                        name: "FK_ImportFile_ImportRun_ImportRunId",
                        column: x => x.ImportRunId,
                        principalTable: "ImportRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExcelSession_UnitId_ExportedAtUtc",
                table: "ExcelSession",
                columns: new[] { "UnitId", "ExportedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExcelTemplate");

            migrationBuilder.DropTable(
                name: "ImportFile");

            migrationBuilder.DropIndex(
                name: "IX_ExcelSession_UnitId_ExportedAtUtc",
                table: "ExcelSession");

            migrationBuilder.DropColumn(
                name: "RowsInFile",
                table: "ImportRun");

            migrationBuilder.DropColumn(
                name: "AssetTag",
                table: "ImportChange");

            migrationBuilder.DropColumn(
                name: "RowNumber",
                table: "ImportChange");

            migrationBuilder.DropColumn(
                name: "BaseJson",
                table: "ExcelSession");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "ExcelSession");

            migrationBuilder.DropColumn(
                name: "RowCount",
                table: "ExcelSession");

            migrationBuilder.CreateIndex(
                name: "IX_ExcelSession_UnitId",
                table: "ExcelSession",
                column: "UnitId");
        }
    }
}
