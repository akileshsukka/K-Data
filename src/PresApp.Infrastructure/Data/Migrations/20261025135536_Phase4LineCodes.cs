using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4LineCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LineCode",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineCode", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LineCode_Kind_Code",
                table: "LineCode",
                columns: new[] { "Kind", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineCode");
        }
    }
}
