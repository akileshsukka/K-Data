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
/// Template v5: the unit file has a second list, EIList, for the Electrical &amp; Instrumentation items with their steps
/// E1–E8 (stored in G1–G8). The static and the electrical engineers each download their own file and import it;
/// neither import touches what the other one changed.
/// </summary>
public sealed class TemplateV5Tests : IClassFixture<TestApp>
{
    private readonly TestApp app;
    private readonly HttpClient admin;

    public TemplateV5Tests(TestApp app)
    {
        this.app = app;
        admin = app.ClientFor(TestApp.AdminEmail);
    }

    [Fact]
    public async Task Static_and_electrical_engineers_import_their_own_files_without_overwriting_each_other()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 42").Select(u => u.Id).FirstAsync());
        var template = await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v5.xlsx"));
        var upload = await admin.PutAsync($"/api/excel-templates/{unitId}", File(template, "Template_Unit_42.xlsx"));
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());

        var pump = await CreateAsync(unitId, "42-P-501", "Static", "Equipment");
        var panel = await CreateAsync(unitId, "42-EP-501", "Electrical", "ElectricalInstrument");
        // Values both engineers start from.
        await PatchAsync(pump, "G1Status", null, "Ongoing");
        await PatchAsync(panel, "G1Status", null, "Ongoing");

        // ---- each engineer downloads a file
        var staticFile = await ExportAsync(unitId);
        var electricalFile = await ExportAsync(unitId);

        using (var wb = new XLWorkbook(new MemoryStream(staticFile)))
        {
            var master = TestWorkbooks.Master(wb);
            var ei = Table(wb, "EIList");
            Assert.Equal(["42-P-501"], Tags(master));          // MasterList: static and rotating only
            Assert.Equal(["42-EP-501"], Tags(ei));              // EIList: the E&I items
            Assert.DoesNotContain(master.DataRange.Rows(), r => r.WorksheetRow().IsHidden);
            var headers = ei.HeadersRow().Cells().Select(c => c.GetString()).ToList();
            Assert.Equal(Enumerable.Range(1, 8).Select(g => $"E{g}-Status"), headers.Where(h => h.StartsWith('E') && h.EndsWith("-Status")));
            Assert.Contains("Key", headers);
            Assert.True(ei.Worksheet.Column(ei.HeadersRow().Cells().First(c => c.GetString() == "Key").Address.ColumnNumber).IsHidden);
            Assert.Equal("Ongoing", EiRow(wb, "42-EP-501").Field("E1-Status").GetString());
            Assert.True(EiRow(wb, "42-EP-501").Field("E7-Status").HasDataValidation);   // same dropdown as the G columns
        }

        // ---- static engineer: only the MasterList. Electrical engineer: only the EIList (E4 and E7).
        var staticEdited = TestWorkbooks.Edit(staticFile, wb => TestWorkbooks.Row(wb, "42-P-501").Field("G1-Status").SetValue("Completed"));
        var electricalEdited = TestWorkbooks.Edit(electricalFile, wb =>
        {
            EiRow(wb, "42-EP-501").Field("E1-Status").SetValue("Completed");
            EiRow(wb, "42-EP-501").Field("E4-Status").SetValue("Completed");
            EiRow(wb, "42-EP-501").Field("E7-Status").SetValue("Completed");
        });

        var first = await ImportAsync(unitId, staticEdited);
        Assert.Equal(["42-P-501:G1Status:Applied"], first.Where(c => c.Outcome == "Applied").Select(c => $"{c.Tag}:{c.Field}:{c.Outcome}"));

        // The electrical file was downloaded before the static import: the pump cell is untouched there, so the pump keeps Completed.
        var second = await ImportAsync(unitId, electricalEdited);
        Assert.Contains(("42-EP-501", "G4Status", "Applied"), second.Select(c => (c.Tag, c.Field, c.Outcome)));
        Assert.Contains(("42-EP-501", "G7Status", "Applied"), second.Select(c => (c.Tag, c.Field, c.Outcome)));
        Assert.DoesNotContain(second, c => c.Tag == "42-P-501");
        Assert.DoesNotContain(second, c => c.Outcome == "Warning");          // no dew point / O2 / N2 reading asked for E steps
        Assert.DoesNotContain(second, c => c.Field == "N2BlanketedDate");     // E4 is not an N2 blanket

        var pumpNow = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{pump}");
        var panelNow = await admin.GetFromJsonAsync<JsonElement>($"/api/assets/{panel}");
        Assert.Equal("Completed", pumpNow.GetProperty("values").GetProperty("G1Status").GetString());
        Assert.Equal("G1", pumpNow.GetProperty("currentGate").GetString());
        Assert.Equal("Completed", panelNow.GetProperty("values").GetProperty("G7Status").GetString());
        Assert.Equal("G7", panelNow.GetProperty("currentGate").GetString());   // shown as E7 for E&I items
        Assert.Null(await app.WithDbAsync(db => db.Preservations.Where(p => p.AssetId == panel).Select(p => p.N2BlanketedDate).FirstAsync()));
    }

    [Fact]
    public async Task A_template_without_the_EI_list_keeps_the_EI_rows_hidden_in_the_master_list()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 41").Select(u => u.Id).FirstAsync());
        var template = await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v4.xlsx"));
        Assert.True((await admin.PutAsync($"/api/excel-templates/{unitId}", File(template, "Template_Unit_41.xlsx"))).IsSuccessStatusCode);
        await CreateAsync(unitId, "41-P-501", "Static", "Equipment");
        await CreateAsync(unitId, "41-EP-501", "Electrical", "ElectricalInstrument");

        using var wb = new XLWorkbook(new MemoryStream(await ExportAsync(unitId)));
        Assert.Null(wb.Worksheets.SelectMany(w => w.Tables).FirstOrDefault(t => t.Name == "EIList"));
        Assert.Equal(["41-EP-501", "41-P-501"], Tags(TestWorkbooks.Master(wb)).Order(StringComparer.Ordinal));
        Assert.True(TestWorkbooks.Row(wb, "41-EP-501").WorksheetRow().IsHidden);
        Assert.False(TestWorkbooks.Row(wb, "41-P-501").WorksheetRow().IsHidden);
    }

    [Fact]
    public async Task Onboarding_reads_the_EI_list_too()
    {
        var unitId = await app.WithDbAsync(db => db.Units.Where(u => u.Name == "Unit 31").Select(u => u.Id).FirstAsync());
        var list = TestWorkbooks.Edit(await System.IO.File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Support", "Template_Unit_39_v5.xlsx")), wb =>
        {
            var master = TestWorkbooks.Master(wb).DataRange.Row(1);
            master.Field("EQUIP NO").SetValue("31-V-777"); master.Field("Discipline").SetValue("Static"); master.Field("G2-Status").SetValue("Completed");
            var ei = Table(wb, "EIList").DataRange.Row(1);
            ei.Field("EQUIP NO").SetValue("31-EP-777"); ei.Field("Discipline").SetValue("Electrical"); ei.Field("E6-Status").SetValue("Completed");
        });
        var response = await admin.PostAsync($"/api/units/{unitId}/excel/onboarding", File(list, "client list.xlsx"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True((await admin.PostAsync($"/api/imports/{preview.GetProperty("run").GetProperty("id").GetInt64()}/apply", null)).IsSuccessStatusCode);

        var created = await app.WithDbAsync(db => db.Assets.Include(a => a.Preservation).Where(a => a.UnitId == unitId && (a.Tag == "31-V-777" || a.Tag == "31-EP-777")).ToListAsync());
        Assert.Equal("Completed", created.Single(a => a.Tag == "31-V-777").Preservation!.G2Status);
        var item = created.Single(a => a.Tag == "31-EP-777");
        Assert.Equal(AssetType.ElectricalInstrument, item.Type);
        Assert.Equal("Completed", item.Preservation!.G6Status);
    }

    // ------------------------------------------------------------------ helpers

    private static IXLTable Table(XLWorkbook wb, string name) => wb.Worksheets.SelectMany(w => w.Tables).First(t => t.Name == name);

    private static IXLTableRow EiRow(XLWorkbook wb, string tag) => Table(wb, "EIList").DataRange.Rows().First(r => r.Field("EQUIP NO").GetString() == tag);

    private static List<string> Tags(IXLTable table) =>
        table.DataRange.Rows().Select(r => r.Field("EQUIP NO").GetString()).Where(t => t.Length > 0).ToList();

    private async Task<Guid> CreateAsync(int unitId, string tag, string discipline, string type)
    {
        var disciplineId = await app.WithDbAsync(db => db.Disciplines.Where(d => d.Name == discipline).Select(d => d.Id).FirstAsync());
        var created = await admin.PostAsJsonAsync("/api/assets", new { type, unitId, disciplineId, tag, description = "Item " + tag });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task PatchAsync(Guid id, string field, string? from, string to)
    {
        var patch = await admin.PatchAsJsonAsync($"/api/assets/{id}", new { changes = new[] { new { field, from, to } } });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
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
        Assert.Equal("Previewed", run.GetProperty("status").GetString());
        var applied = await admin.PostAsync($"/api/imports/{run.GetProperty("id").GetInt64()}/apply", null);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var result = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Applied", result.GetProperty("run").GetProperty("status").GetString());
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
