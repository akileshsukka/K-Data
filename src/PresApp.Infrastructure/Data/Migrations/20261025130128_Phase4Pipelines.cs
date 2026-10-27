using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Pipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Insulation",
                table: "PipelineDetail",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "PipelineDetail",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Insulation",
                table: "PipelineDetail");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "PipelineDetail");
        }
    }
}
