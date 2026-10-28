using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PresApp.Infrastructure.Seeding;
using PresApp.Domain.Security;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>A Viewer sees the whole Unit overview (piping cards included), which is also what the PDF export prints.</summary>
public sealed class ViewerDashboardTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    public ViewerDashboardTests(TestApp app) => this.app = app;

    private static readonly string[] Headers =
        ["Sr. No.", "Tag No", "P&ID Number", "Process Code", "Process Description", "Current Activity", "EM Walkdown Status", "Bulk liquid drained", "Inspection Done ?", "Cleaning Required ?", "Cleaned?"];

    private static byte[] LineList(params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Line List");
        for (var c = 0; c < Headers.Length; c++) ws.Cell(2, c + 2).SetValue(Headers[c]);
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++) ws.Cell(3 + r, c + 2).SetValue(XLCellValue.FromObject(rows[r][c]));
        ws.Range(2, 2, 2 + rows.Length, 1 + Headers.Length).CreateTable("Table1");
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Viewer_sees_the_dashboard_and_the_piping_cards()
    {
        var admin = app.ClientFor(TestApp.AdminEmail);
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 39").Select(u => u.Id).FirstAsync());
        var file = new ByteArrayContent(LineList(
            [1, "39-0001-HC-2\"-61055-N", null, "HC", "", null, "Completed", "Yes", "Yes", "Hydromilling", "Yes"],
            [2, "39-0002-LS-2\"-61055-N", null, "LS", "", null, null, null, "No", "TBC", null]));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var preview = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", new MultipartFormDataContent { { file, "file", "list.xlsx" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null)).EnsureSuccessStatusCode();

        const string email = "viewer.dashboard@enermech.com";
        await app.AddUserAsync(email, RoleNames.Viewer);
        var viewer = app.ClientFor(email);

        var me = await viewer.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Contains(me.GetProperty("permissions").EnumerateArray(), p => p.GetString() == Permissions.AssetsView);

        var dashboard = await viewer.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard");
        Assert.Equal(2, dashboard.GetProperty("lineCount").GetInt32());

        var summary = await viewer.GetAsync($"/api/units/{unitId}/piping-summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var s = await summary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, s.GetProperty("lines").GetInt32());
        Assert.Equal(1, s.GetProperty("walkdown").GetProperty("done").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/units/{unitId}/piping-notes")).StatusCode);
    }

    [Fact]
    public async Task The_seeder_gives_assets_view_back_to_the_Viewer_role()
    {
        await app.WithDbAsync(db => db.Set<RolePermission>()
            .Where(p => p.Role.Name == RoleNames.Viewer && p.Permission == Permissions.AssetsView).ExecuteDeleteAsync());

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(TestApp.AdminEmail);

        Assert.True(await app.WithDbAsync(db => db.Set<RolePermission>()
            .AnyAsync(p => p.Role.Name == RoleNames.Viewer && p.Permission == Permissions.AssetsView)));
    }
}
