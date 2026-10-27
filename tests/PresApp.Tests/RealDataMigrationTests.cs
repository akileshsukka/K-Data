using Microsoft.Extensions.DependencyInjection;
using PresApp.Infrastructure.SharePointMigration;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// Runs the migration on a real SharePoint export when PRESAPP_MIGRATION_FOLDER points to it.
/// Skipped otherwise (the export is not part of the repository).
/// </summary>
public sealed class RealDataMigrationTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    public RealDataMigrationTests(TestApp app) => this.app = app;

    [Fact]
    public async Task Real_export_loads_and_counts_match()
    {
        var folder = Environment.GetEnvironmentVariable("PRESAPP_MIGRATION_FOLDER");
        if (string.IsNullOrEmpty(folder)) return;

        await using var scope = app.Services.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<SharePointImporter>().RunAsync(folder, apply: true);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "presapp-migration-report.md"), report.ToMarkdown());

        Assert.True(report.CountsMatch, report.ToMarkdown());
    }
}
