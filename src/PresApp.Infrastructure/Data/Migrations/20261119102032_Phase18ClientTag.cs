using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase18ClientTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientTag",
                table: "Asset",
                type: "nvarchar(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Asset_ClientTag",
                table: "Asset",
                column: "ClientTag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Asset_ClientTag",
                table: "Asset");

            migrationBuilder.DropColumn(
                name: "ClientTag",
                table: "Asset");
        }
    }
}
