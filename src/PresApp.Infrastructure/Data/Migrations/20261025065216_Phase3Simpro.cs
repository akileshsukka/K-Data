using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Simpro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NotInAppCount",
                table: "SimproSyncRun",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AssetTag",
                table: "SimproSyncItem",
                type: "nvarchar(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncedValue",
                table: "SimproSyncItem",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnitId",
                table: "SimproSyncItem",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SimproForcedAsset",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimproForcedAsset", x => x.AssetId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimproForcedAsset");

            migrationBuilder.DropColumn(
                name: "NotInAppCount",
                table: "SimproSyncRun");

            migrationBuilder.DropColumn(
                name: "AssetTag",
                table: "SimproSyncItem");

            migrationBuilder.DropColumn(
                name: "LastSyncedValue",
                table: "SimproSyncItem");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "SimproSyncItem");
        }
    }
}
