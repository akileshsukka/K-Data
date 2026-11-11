using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PresApp.Domain.Reporting;
using PresApp.Tests.Support;

namespace PresApp.Tests;

/// <summary>
/// Template v6 = v5 plus the E&amp;I dashboard: an EICategories table (codes CatE1…) and an "E&amp;I Dashboard" sheet over the
/// EIList, whose category rows are the named ranges KD_EI_HoldSlots / KD_EI_NASlots. Each list offers only its own codes.
/// </summary>
public sealed class TemplateV6Tests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public TemplateV6Tests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task The_ei_catalogue_fills_its_own_table_and_dashboard_rows_and_is_imported_from_the_ei_list()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 42").Select(u => u.Id).FirstAsync());
        var template = await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v6.xlsx"));
        var upload = await admin.PutAsync($"/api/excel-templates/{unitId}", File(template, "Template_Unit_42.xlsx"));
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());

        var ei = await SaveAsync(unitId, "ei", new { label = "Motor removed", section = "Hold" }, new { label = "Spare motor", section = "NotApplicable" });
        // The mechanical list (whatever the template brought is replaced): its codes never reach the EIList.
        await SaveAsync(unitId, null, new { label = "Vessel open", section = "Hold" });
        var motorRemoved = ei.Single(c => c.GetProperty("label").GetString() == "Motor removed").GetProperty("id").GetInt32();

        var motor = await CreateAsync(unitId, "42-EM-601", "Electrical", "ElectricalInstrument");
        var spare = await CreateAsync(unitId, "42-EM-602", "Electrical", "ElectricalInstrument");
        await CreateAsync(unitId, "42-V-601", "Static", "Equipment");
        Assert.True((await admin.PutAsJsonAsync($"/api/assets/{motor}/dashboard-category", new { categoryId = motorRemoved, expected = (int?)null })).IsSuccessStatusCode);

        var file = await ExportAsync(unitId);
        using (var wb = new XLWorkbook(new MemoryStream(file)))
        {
            var eiCategories = Table(wb, "EICategories");
            Assert.Equal(["CatE1", "CatE1"], eiCategories.DataRange.Rows().Select(r => r.Field("Code").GetString()));
            Assert.Equal(["Hold", "N/A"], eiCategories.DataRange.Rows().Select(r => r.Field("Section").GetString()));
            Assert.DoesNotContain(Table(wb, "Categories").DataRange.Rows(), r => r.Field("Code").GetString().StartsWith("CatE"));

            // One E&I dashboard row per category, the others hidden.
            var dashboard = wb.Worksheet("E&I Dashboard");
            var holdSlots = wb.DefinedNames.First(n => n.Name == "KD_EI_HoldSlots").Ranges.First();
            Assert.False(holdSlots.Row(1).WorksheetRow().IsHidden);
            Assert.True(holdSlots.Row(2).WorksheetRow().IsHidden);
            Assert.StartsWith("UNIT-", dashboard.Cell("C2").GetString());
            Assert.DoesNotContain("{DATE}", dashboard.Cell("C2").GetString());

            // The EIList offers the CatE codes only; the MasterList the Cat codes only.
            var eiRow = Table(wb, "EIList").DataRange.Rows().First(r => r.Field("EQUIP NO").GetString() == "42-EM-601");
            Assert.Equal("CatE1", eiRow.Field("Hold Category").GetString());
            Assert.Contains("EIHoldCategory", ListSource(eiRow.Field("Hold Category")));
            var masterRow = TestWorkbooks.Master(wb).DataRange.Rows().First(r => r.Field("EQUIP NO").GetString() == "42-V-601");
            Assert.DoesNotContain("EIHoldCategory", ListSource(masterRow.Field("Hold Category")));
        }

        // ---- the electrical engineer moves the spare motor to N/A CatE1; a mechanical code in the EIList is refused
        var edited = TestWorkbooks.Edit(file, wb =>
        {
            var rows = Table(wb, "EIList").DataRange.Rows().ToList();
            rows.First(r => r.Field("EQUIP NO").GetString() == "42-EM-602").Field("N/A Category").SetValue("CatE1");
            rows.First(r => r.Field("EQUIP NO").GetString() == "42-EM-601").Field("Hold Category").SetValue("Cat1");
        });
        var changes = await ImportAsync(unitId, edited);
        Assert.Contains(("42-EM-602", "DashboardCategory", "Applied"), changes);
        Assert.Contains(("42-EM-601", "DashboardCategory", "Invalid"), changes);

        var spareCategory = await app.WithDbAsync(db => db.Preservations.Where(p => p.AssetId == spare).Select(p => p.DashboardCategory!).FirstAsync());
        Assert.Equal(CategoryScope.ElectricalInstrument, spareCategory.Scope);
        Assert.Equal(CategorySection.NotApplicable, spareCategory.Section);
        var d = await admin.GetFromJsonAsync<JsonElement>($"/api/units/{unitId}/dashboard?scope=ei");
        Assert.Equal(1, d.GetProperty("totalHold").GetInt32());
        Assert.Equal(1, d.GetProperty("totalNotApplicable").GetInt32());
    }

    // ------------------------------------------------------------------ helpers

    private static IXLTable Table(XLWorkbook wb, string name) => wb.Worksheets.SelectMany(w => w.Tables).First(t => t.Name == name);

    /// <summary>Key of the _lists column a dropdown points to (its header), e.g. "EIHoldCategory".</summary>
    private static string ListSource(IXLCell cell)
    {
        var source = cell.Worksheet.DataValidations.FirstOrDefault(v => v.Ranges.Any(r => r.Contains(cell)))?.Value ?? "";
        var bang = source.IndexOf('!');
        if (bang < 0) return source;
        var column = source[(bang + 1)..].Split(':')[0].Replace("$", "").TrimEnd("0123456789".ToCharArray());
        return cell.Worksheet.Workbook.Worksheet(source[..bang].Trim('\'')).Cell($"{column}1").GetString();
    }

    private async Task<List<JsonElement>> SaveAsync(int unitId, string? scope, params object[] categories)
    {
        var response = await admin.PutAsJsonAsync($"/api/units/{unitId}/categories{(scope is null ? "" : $"?scope={scope}")}", categories);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<Guid> CreateAsync(int unitId, string tag, string discipline, string type)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<byte[]> ExportAsync(int unitId)
    {
        var response = await admin.GetAsync($"/api/units/{unitId}/excel/export");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<List<(string? Tag, string? Field, string? Outcome)>> ImportAsync(int unitId, byte[] file)
    {
        var response = await admin.PostAsync($"/api/units/{unitId}/excel/import", File(file, "edited.xlsx"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        var run = preview.GetProperty("run");
        var applied = await admin.PostAsync($"/api/imports/{run.GetProperty("id").GetInt64()}/apply", null);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("changes").EnumerateArray()
            .Select(x => (x.GetProperty("assetTag").GetString(), x.GetProperty("field").GetString(), x.GetProperty("outcome").GetString())).ToList();
    }

    private static MultipartFormDataContent File(byte[] bytes, string name)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { file, "file", name } };
    }
}
