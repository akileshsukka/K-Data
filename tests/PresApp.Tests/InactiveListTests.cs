using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Assets;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// "Inactive only" in a register shows the deactivated assets alone, and the rows on screen can be downloaded in the
/// register's template (unit file or line list) as a report: only the list sheet, "Deactivated on / by", never imported.
/// </summary>
public sealed class InactiveListTests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public InactiveListTests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task Inactive_only_shows_the_deactivated_assets_alone()
    {
        var unitId = await UnitIdAsync("Unit 32");
        var kept = await CreateAsync(unitId, "32-V-IN1", "Static", "Equipment");
        var off = await CreateAsync(unitId, "32-V-IN2", "Static", "Equipment");
        await DeactivateAsync(off);

        var inactive = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Equipment&inactiveOnly=true");
        var ids = inactive.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(off, ids);
        Assert.DoesNotContain(kept, ids);
        Assert.All(inactive.EnumerateArray(), r => Assert.False(r.GetProperty("isActive").GetBoolean()));

        var active = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/assets?type=Equipment");
        Assert.Contains(kept, active.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(off, active.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task The_inactive_ei_items_go_to_the_ei_list_of_the_template_without_dashboard_and_cannot_be_imported()
    {
        var unitId = await UnitIdAsync("Unit 42");
        var template = await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v6.xlsx"));
        Assert.True((await admin.PutAsync($"/api/excel-templates/{unitId}", File(template, "Template_Unit_42.xlsx"))).IsSuccessStatusCode);

        var motor = await CreateAsync(unitId, "42-EM-701", "Electrical", "ElectricalInstrument");
        var other = await CreateAsync(unitId, "42-EM-702", "Electrical", "ElectricalInstrument");
        var active = await CreateAsync(unitId, "42-EM-703", "Electrical", "ElectricalInstrument");
        await DeactivateAsync(motor, other);

        // Only the rows on screen (here one of the two inactive items); an active id sent by mistake is not listed.
        var response = await admin.PostAsJsonAsync($"/api/units/{unitId}/excel/inactive/export",
            new { type = "ElectricalInstrument", assetIds = new[] { motor, active } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Contains("INACTIVE", response.Content.Headers.ContentDisposition?.FileName ?? "");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var visible = wb.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).ToList();
            var sheet = Assert.Single(visible);
            Assert.DoesNotContain(wb.Worksheets, w => w.Name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(wb.DefinedNames, n => n.Name.StartsWith("KD_", StringComparison.Ordinal));
            var table = sheet.Tables.Single(t => t.Name == "EIList");
            var row = Assert.Single(table.DataRange.Rows());
            Assert.Equal("42-EM-701", row.Field("EQUIP NO").GetString());
            Assert.Equal(DateTime.UtcNow.Year, row.Field("Deactivated on").GetDateTime().Year);
            Assert.Equal(TestApp.AdminEmail, row.Field("Deactivated by").GetString(), ignoreCase: true);
            Assert.StartsWith("INACTIVE ASSETS - ", sheet.Cell("B2").GetString());
            Assert.Equal("InactiveList", wb.Worksheet("_meta").Cell("B1").GetString());
        }

        // No session: the update import and the onboarding both refuse it.
        var import = await (await admin.PostAsync($"/api/units/{unitId}/excel/import", File(bytes, "inactive.xlsx"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rejected", import.GetProperty("run").GetProperty("status").GetString());
        Assert.Contains("inactive assets", import.GetProperty("run").GetProperty("message").GetString());
        var onboarding = await (await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File(bytes, "inactive.xlsx"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rejected", onboarding.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(0, await app.WithDbAsync(db => db.ExcelSessions.CountAsync(s => s.UnitId == unitId)));

        // Nothing inactive on screen: nothing to download.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/units/{unitId}/excel/inactive/export",
            new { type = "ElectricalInstrument", assetIds = new[] { active } })).StatusCode);
    }

    [Fact]
    public async Task The_inactive_lines_go_to_the_line_list_without_its_summary()
    {
        var unitId = await UnitIdAsync("Unit 41");
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.AssetType == AssetType.Pipeline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type = "Pipeline", unitId, disciplineId, tag = "41-0901-HCX-6\"-61055-N" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var line = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DeactivateAsync(line);

        var response = await admin.PostAsJsonAsync($"/api/units/{unitId}/excel/inactive/export", new { type = "Pipeline", assetIds = new[] { line } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var wb = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = Assert.Single(wb.Worksheets, w => w.Visibility == XLWorksheetVisibility.Visible);
        Assert.Equal("Line List", sheet.Name);
        Assert.False(wb.TryGetWorksheet("Summary", out _));
        var row = Assert.Single(sheet.Tables.First().DataRange.Rows());
        Assert.Equal("41-0901-HCX-6\"-61055-N", row.Field("Tag No").GetString());
        Assert.Equal(TestApp.AdminEmail, row.Field("Deactivated by").GetString(), ignoreCase: true);
    }

    // ------------------------------------------------------------------ helpers

    private Task<int> UnitIdAsync(string name) => app.WithDbAsync(db => db.Units.Where(u => u.Name == name).Select(u => u.Id).FirstAsync());

    private async Task<Guid> CreateAsync(int unitId, string tag, string discipline, string type)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task DeactivateAsync(params Guid[] ids) =>
        (await admin.PostAsJsonAsync("/api/assets/active", new { assetIds = ids, active = false })).EnsureSuccessStatusCode();

    private static MultipartFormDataContent File(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }
}
