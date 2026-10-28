using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PresApp.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lines get their own Current Activity list (piping values). Equipment values that lines got before
    /// (e.g. "Draining") are cleared once, with a history row; the next upload of the line list fills the right ones.
    /// From now on the API only accepts the piping list for lines.
    /// </summary>
    public partial class Phase8PipingLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            const string piping = "N'Line cut', N'Debris inside; jetting required', N'Preservation ongoing', N'Pump suction spool removed', N'Line dropped', N'Spool dropped'";
            migrationBuilder.Sql($"""
                DECLARE @batch uniqueidentifier = NEWID();
                INSERT INTO [ChangeLog] ([EntityType], [EntityKey], [AssetId], [Field], [OldValue], [NewValue], [ChangedAtUtc], [ChangedBy], [Source], [BatchId])
                SELECT N'AssetPreservation', LOWER(CONVERT(nvarchar(36), p.[AssetId])), p.[AssetId], N'CurrentActivity', p.[CurrentActivity], NULL,
                       SYSUTCDATETIME(), N'K Data (piping lists)', N'Migration', @batch
                FROM [AssetPreservation] p JOIN [Asset] a ON a.[Id] = p.[AssetId]
                WHERE a.[Type] = N'Pipeline' AND p.[CurrentActivity] IS NOT NULL AND p.[CurrentActivity] NOT IN ({piping});

                UPDATE p SET p.[CurrentActivity] = NULL
                FROM [AssetPreservation] p JOIN [Asset] a ON a.[Id] = p.[AssetId]
                WHERE a.[Type] = N'Pipeline' AND p.[CurrentActivity] IS NOT NULL AND p.[CurrentActivity] NOT IN ({piping});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The cleared values stay in ChangeLog (Source = Migration); they are not restored automatically.
        }
    }
}
