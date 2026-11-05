using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase12NonStandardLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NonStandardNumber",
                table: "PipelineDetail",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NumberNote",
                table: "PipelineDetail",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LineFormatCheckOff",
                table: "ImportRun",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NonStandardNumber",
                table: "PipelineDetail");

            migrationBuilder.DropColumn(
                name: "NumberNote",
                table: "PipelineDetail");

            migrationBuilder.DropColumn(
                name: "LineFormatCheckOff",
                table: "ImportRun");
        }
    }
}
