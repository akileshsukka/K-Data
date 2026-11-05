using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase13ElectricalTest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NameplateData",
                table: "Asset",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ElectricalTest",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    TestedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    WrU1U2 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WrV1V2 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WrW1W2 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IrUV = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IrUW = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IrVW = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IrUG = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IrVG = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IrWG = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarUVwe30 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarUVwe60 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarVUwe30 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarVUwe60 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarWUve30 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarWUve60 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarUvwToE30 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DarUvwToE60 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Pi1 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Pi10 = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectricalTest", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_ElectricalTest_Asset_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Asset",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectricalTest_Required",
                table: "ElectricalTest",
                column: "Required");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElectricalTest");

            migrationBuilder.DropColumn(
                name: "NameplateData",
                table: "Asset");
        }
    }
}
